using System.Text;
using System.Threading.Channels;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LiveTranscribeRu;

/// <summary>Какой движок Whisper использовать.</summary>
public enum WhisperBackend { Auto, Gpu, Cpu }

/// <summary>
/// Точное уточнение сегментов через Whisper. Vosk выдаёт «живой» черновой текст
/// мгновенно, а аудио готового сегмента ставится в очередь сюда: Whisper
/// перераспознаёт фрагмент целиком и присылает точный текст, которым в UI
/// заменяется черновик. Очередь обрабатывается строго по одному (Whisper не
/// потокобезопасен), порядок сегментов сохраняется.
/// </summary>
public sealed class WhisperRefiner
{
    public event Action<long, string>? SegmentRefined;   // (id сегмента, уточнённый текст)
    public event Action<string>? Log;

    readonly string _modelPath;
    readonly string _language;
    readonly WhisperBackend _backend;
    readonly TimeSpan _refinementTimeout;
    readonly TimeSpan _drainTimeout;
    readonly bool _timeoutIncludesQueueWait;
    readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    WhisperFactory? _factory;
    WhisperProcessor? _processor;
    Channel<(long id, float[] samples, DateTimeOffset queuedAt)>? _queue;
    Task? _consumer;
    CancellationTokenSource? _cts;

    /// <summary>Какой движок реально загрузился ("Cuda" / "Cpu" / …). Заполняется после StartAsync.</summary>
    public string LoadedBackend { get; private set; } = "?";
    public bool DrainCompleted { get; private set; }

    readonly string? _prompt;
    SileroVad? _vad;   // обрезка сегмента до чистой речи перед Whisper (меньше галлюцинаций, точнее)

    public WhisperRefiner(string modelPath, WhisperBackend backend, string language = "ru",
        string? prompt = null, string? vadModelPath = null,
        TimeSpan? refinementTimeout = null, TimeSpan? drainTimeout = null,
        bool timeoutIncludesQueueWait = true)
    {
        _modelPath = modelPath;
        _backend = backend;
        _language = language;
        _refinementTimeout = refinementTimeout ?? TimeSpan.FromSeconds(30);
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(4);
        _timeoutIncludesQueueWait = timeoutIncludesQueueWait;
        _prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        if (vadModelPath is not null && File.Exists(vadModelPath))
            try { _vad = new SileroVad(vadModelPath); } catch { _vad = null; }
    }

    public bool ModelExists => File.Exists(_modelPath);

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    async Task StartCoreAsync()
    {
        if (_cts is not null) return;
        DrainCompleted = false;
        _cts = new CancellationTokenSource();
        _queue = Channel.CreateBounded<(long, float[], DateTimeOffset)>(
            new BoundedChannelOptions(24)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        try
        {
            // выбор backend: при недоступности CUDA Whisper сам откатится на следующий в списке (CPU)
            RuntimeOptions.RuntimeLibraryOrder = _backend switch
            {
                WhisperBackend.Cpu => new List<RuntimeLibrary> { RuntimeLibrary.Cpu },
                WhisperBackend.Gpu => new List<RuntimeLibrary> { RuntimeLibrary.Cuda, RuntimeLibrary.Cpu },
                _ => new List<RuntimeLibrary> { RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
            };

            await Task.Run(() =>
            {
                _factory = WhisperFactory.FromPath(_modelPath);
                var b = _factory.CreateBuilder()
                    .WithLanguage(_language)
                    .WithThreads(Math.Max(1, Environment.ProcessorCount - 1));
                if (_prompt is not null) b = b.WithPrompt(_prompt);   // словарь терминов — правильное написание имён/жаргона
                _processor = b.Build();
                LoadedBackend = RuntimeOptions.LoadedLibrary?.ToString() ?? "Cpu";
            });

            _consumer = Task.Run(() => ConsumeAsync(_processor!, _factory!, _cts!));
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
    }

    /// <summary>Поставить сегмент в очередь на уточнение (16 кГц моно float, [-1..1]).</summary>
    public void Enqueue(long id, float[] samples)
    {
        var accepted = _queue?.Writer.TryWrite(
            (id, samples, DateTimeOffset.UtcNow)) == true;
        if (accepted) return;
        Log?.Invoke($"whisper queue full: draft retained for #{id}");
        SegmentRefined?.Invoke(id, "");
    }

    public async Task EnqueueAsync(long id, float[] samples, CancellationToken cancellationToken = default)
    {
        var writer = _queue?.Writer
                     ?? throw new InvalidOperationException("Whisper has not started.");
        await writer.WriteAsync((id, samples, DateTimeOffset.UtcNow), cancellationToken);
    }

    public void CancelPending()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    const int MinReliableSamples = 9600;   // < 0.6 c — слишком коротко (Whisper галлюцинирует), не уточняем

    // типичные «титровые» галлюцинации Whisper на тишине/шуме (нормализованные: lower, без пунктуации)
    static readonly HashSet<string> Hallucinations = new()
    {
        "добро пожаловать в казахстан",
        "спасибо за просмотр",
        "спасибо за внимание",
        "продолжение следует",
        "подписывайтесь на канал",
        "редактор субтитров а семкин корректор а егорова",
        "субтитры сделал dimatorzok",
        "субтитры создавал dimatorzok",
        "продолжение в следующей серии",
        "до новых встреч",
        "всем пока",
    };

    static bool IsHallucination(string text)
    {
        var norm = new string(text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
            .Replace("  ", " ").Trim();
        if (Hallucinations.Contains(norm)) return true;
        // явные «субтитры …» тоже почти всегда галлюцинации
        return norm.StartsWith("субтитры") || norm.Contains("dimatorzok");
    }

    /// <summary>Обрезает сегмент до речевой части по Silero VAD (с небольшими полями).</summary>
    float[] TrimToSpeech(float[] s)
    {
        const float Th = 0.5f;
        const int Pad = 4;        // ~128 мс полей с каждой стороны
        int F = SileroVad.FrameSamples;
        int nFrames = s.Length / F;
        if (nFrames == 0) return s;

        _vad!.Reset();
        var frame = new float[F];
        int first = -1, last = -1;
        for (int i = 0; i < nFrames; i++)
        {
            Array.Copy(s, i * F, frame, 0, F);
            if (_vad.Probability(frame) >= Th) { if (first < 0) first = i; last = i; }
        }
        if (first < 0) return Array.Empty<float>();   // речи не найдено

        int startF = Math.Max(0, first - Pad);
        int endF = Math.Min(nFrames - 1, last + Pad);
        int start = startF * F;
        int len = Math.Min((endF - startF + 1) * F, s.Length - start);
        var outp = new float[len];
        Array.Copy(s, start, outp, 0, len);
        return outp;
    }

    async Task ConsumeAsync(WhisperProcessor processor, WhisperFactory factory, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var reader = _queue!.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var job))
                {
                    var remaining = _timeoutIncludesQueueWait
                        ? _refinementTimeout - (DateTimeOffset.UtcNow - job.queuedAt)
                        : _refinementTimeout;
                    if (remaining <= TimeSpan.Zero)
                    {
                        Log?.Invoke($"whisper timeout before processing #{job.id}");
                        SegmentRefined?.Invoke(job.id, "");
                        continue;
                    }

                    // короткие куски Whisper галлюцинирует («Добро пожаловать в Казахстан» и т.п.) —
                    // их не уточняем, оставляем черновик Vosk (пустой результат = «снять „уточняю“»)
                    if (job.samples.Length < MinReliableSamples)
                    {
                        SegmentRefined?.Invoke(job.id, "");
                        continue;
                    }

                    // обрезаем тишину/шум по краям сегмента (Silero VAD) — точнее и без галлюцинаций
                    var samples = _vad is not null ? TrimToSpeech(job.samples) : job.samples;
                    if (samples.Length < MinReliableSamples)
                    {
                        SegmentRefined?.Invoke(job.id, "");   // речи не нашлось — оставить черновик Vosk
                        continue;
                    }

                    var sb = new StringBuilder();
                    using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    jobCts.CancelAfter(remaining);
                    try
                    {
                        await foreach (var seg in processor.ProcessAsync(samples, jobCts.Token))
                            sb.Append(seg.Text);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        Log?.Invoke($"whisper timeout during processing #{job.id}");
                        SegmentRefined?.Invoke(job.id, "");
                        continue;
                    }
                    catch (Exception ex) { Log?.Invoke($"whisper error: {ex.Message}"); SegmentRefined?.Invoke(job.id, ""); continue; }

                    var text = sb.ToString().Trim();
                    bool hasLetters = text.Any(char.IsLetterOrDigit);
                    if (text.Length > 0 && hasLetters && !IsHallucination(text))
                    {
                        Log?.Invoke($"whisper refined #{job.id}: chars={text.Length}");
                        SegmentRefined?.Invoke(job.id, text);
                    }
                    else
                    {
                        if (text.Length > 0)
                            Log?.Invoke(
                                $"whisper discarded noise/hallucination #{job.id}: chars={text.Length}");
                        SegmentRefined?.Invoke(job.id, "");   // оставить черновик Vosk
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // consumer сам освобождает ресурсы — остановка не ждёт его (см. StopAsync)
            processor.Dispose();
            factory.Dispose();
            cts.Dispose();
        }
    }

    /// <summary>
    /// Остановка: даём Whisper дослить очередь (чтобы последняя фраза успела уточниться),
    /// но не дольше таймаута — иначе на медленном CPU с большим бэклогом интерфейс завис бы.
    /// На GPU слив занимает доли секунды.
    /// </summary>
    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    async Task StopCoreAsync()
    {
        _queue?.Writer.TryComplete();   // новых сегментов не принимаем
        var consumer = _consumer;
        var cts = _cts;
        var processor = _processor;
        var factory = _factory;
        _processor = null; _factory = null; _queue = null; _consumer = null;

        if (consumer is not null)
        {
            var finished = await Task.WhenAny(consumer, Task.Delay(_drainTimeout));
            DrainCompleted = finished == consumer;
            if (!DrainCompleted) cts?.Cancel();
            try { await consumer; } catch { }
        }
        else
        {
            DrainCompleted = true;
            try { processor?.Dispose(); } catch { }
            try { factory?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }
        }
        _cts = null;
        _vad?.Dispose(); _vad = null;
    }
}
