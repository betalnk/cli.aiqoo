using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vosk;

namespace LiveTranscribeRu;

public enum AudioSource { System, Microphone, Both, RemotePcm }

/// <summary>
/// Потоковое распознавание на Vosk с РАЗДЕЛЬНЫМИ конвейерами по источникам:
/// микрофон («Я») и системный звук (собеседники) распознаются независимо и
/// параллельно — поэтому одновременная речь не смешивается. Готовая фраза каждого
/// источника уточняется через Whisper. Собеседники из системного потока при наличии
/// модели голосовых отпечатков делятся на «Участник 1/2/3» (см. SpeakerDiarizer).
/// </summary>
public sealed class TranscriptionService
{
    static readonly object CaptureLeaseSync = new();
    static TranscriptionService? _captureOwner;

    const int Rate = 16000;
    const int PumpBlock = 320;          // 20 мс при 16 кГц — ровно реальное время (16000/50)
    const string ModelDir = "vosk-model-small-ru-0.22";

    public event Action<string>? Status;
    public event Action<long, DateTime, string, string>? SegmentRecognized;  // (id, время, черновик Vosk, ключ говорящего)
    public event Action<long, DateTime, string, string, bool>? SegmentFinalized; // + будет ли уточнение Whisper
    public event Action<long, string>? SegmentRefined;                       // уточнённый Whisper текст для сегмента
    public event Action<string>? DictationRefined;                            // итог всей локальной диктовки
    public event Action<int, int>? DictationProgress;                         // обработано частей / всего частей
    public event Action<string, string>? PartialResult;                      // (ключ говорящего, «живые» слова)
    public event Action<float>? AudioLevel;

    const string WhisperModel = "ggml-largev3turbo-q5_0.bin";
    const int MaxSegSamples = 16000 * 30;   // Whisper обрабатывает максимум 30 c
    const float Gain = 4f;                  // усиление тихого сигнала (с защитой от клиппинга)

    public const string SpeakerMe = "me";   // микрофон

    /// <summary>Отдельный конвейер одного источника звука.</summary>
    sealed class Src
    {
        public required string Name;
        public required bool IsMic;                   // микрофон → «Я»; иначе системный звук → собеседники
        public required IWaveIn Capture;
        public required BufferedWaveProvider Raw;
        public required ISampleProvider Resampled;    // 16 кГц моно float
        public required VoskRecognizer Rec;           // свой распознаватель на источник
        public readonly List<float> SegBuf = new();   // аудио текущего сегмента (для Whisper и диаризации)
        public string LastPartial = "";
        public string LastSpeakerKey = "";            // последний определённый ключ (для «живого» текста)
        public float MaxLevel;
        public readonly float[] Block = new float[PumpBlock];   // прочитанный блок текущего тика
        public int BlockN;
    }

    readonly List<Src> _sources = new();
    readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    Model? _model;
    Task? _pump;
    CancellationTokenSource? _cts;
    bool _captureLeaseHeld;
    Src? _remotePcmSource;
    long _remotePcmBytes;
    readonly SemaphoreSlim _remoteFeedGate = new(1, 1);

    WhisperRefiner? _whisper;
    bool _dictationMode;
    List<float>? _dictationSamples;
    readonly Dictionary<long, string> _dictationChunkResults = new();
    int _dictationTotalChunks;
    CancellationTokenSource? _dictationCancellation;
    volatile bool _dictationCancelled;
    SpeakerDiarizer? _diarizer;     // делит собеседников системного потока на Участник 1/2/3 (если модель есть)
    EchoCanceller? _aec;            // вычитает системный звук (эхо) из микрофона — для работы без наушников
    TermCorrector? _corrector;      // правит кириллические расшифровки терминов (кибернетис → Kubernetes)
    long _segId;

    const int AecFilterLen = 2048;  // ~128 мс при 16 кГц — покрывает задержку и хвост эха

    // Защита от остаточного эха: подавляем «Я», если система была НАМНОГО громче очищенного микрофона.
    const double EchoGuard = 3.0;
    double _micSegEnergy, _sysConcurEnergy;

    public event Action<string>? LogLine;   // строки лога — для панели диагностики в UI

    const long MaxLogBytes = 2 * 1024 * 1024;
    const int MaxLogArchives = 3;
    static readonly object LogFileSync = new();
    string _logPath = "";

    void Log(string m)
    {
        var safeMessage = string.Join(
            " ",
            (m ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (safeMessage.Length > 1000) safeMessage = safeMessage[..1000] + "…";
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {safeMessage}";
        try
        {
            lock (LogFileSync)
            {
                if (_logPath.Length == 0) PrepareDiagnosticLog();
                RotateDiagnosticLogIfNeeded();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch { }
        LogLine?.Invoke(line);
    }

    public bool IsRunning { get; private set; }
    public bool WhisperActive { get; private set; }

    public static void RemoveLegacyPlaintextLogs()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { paths.Add(Path.GetFullPath("debug.log")); } catch { }
        try { paths.Add(Path.Combine(AppContext.BaseDirectory, "debug.log")); } catch { }
        try { paths.Add(Path.Combine(AppContext.BaseDirectory, "ui.log")); } catch { }
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    public async Task StartAsync(AudioSource source, WhisperBackend backend = WhisperBackend.Auto,
        bool dictationMode = false)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StartCoreAsync(source, backend, dictationMode);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Starts a separate recognition instance fed by 16-bit mono PCM from a paired phone.</summary>
    public async Task StartRemotePcmDictationAsync(int sampleRate,
        WhisperBackend backend = WhisperBackend.Auto)
    {
        if (sampleRate is < 8000 or > 96000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        await _lifecycleGate.WaitAsync();
        try
        {
            if (IsRunning) throw new InvalidOperationException("Recognition is already running.");
            _remotePcmBytes = 0;
            await StartCoreAsync(AudioSource.RemotePcm, backend, dictationMode: true,
                remoteSampleRate: sampleRate);
            if (!IsRunning || !WhisperActive)
                throw new InvalidOperationException("Local phone recognition did not start.");
        }
        finally { _lifecycleGate.Release(); }
    }

    /// <summary>Feeds one bounded PCM chunk; waits for room rather than dropping a fast phone upload.</summary>
    public async Task FeedRemotePcmAsync(ReadOnlyMemory<byte> pcm, CancellationToken token = default)
    {
        if (pcm.Length is < 2 or > 16 * 1024 || (pcm.Length & 1) != 0)
            throw new ArgumentException("Expected 2–16384 even PCM bytes.", nameof(pcm));
        await _remoteFeedGate.WaitAsync(token);
        try
        {
            var source = _remotePcmSource;
            if (!IsRunning || source?.Capture is not RemotePcmWaveIn capture)
                throw new InvalidOperationException("Phone PCM recognition is not running.");
            const long maxSeconds = 120;
            var maxBytes = (long)capture.WaveFormat.SampleRate * 2 * maxSeconds;
            if (_remotePcmBytes + pcm.Length > maxBytes)
                throw new InvalidOperationException("Phone recording exceeds the 120-second limit.");
            while (source.Raw.BufferedBytes + pcm.Length > source.Raw.BufferLength)
            {
                token.ThrowIfCancellationRequested();
                if (!IsRunning) throw new InvalidOperationException("Phone recording stopped.");
                await Task.Delay(20, token);
            }
            capture.Feed(pcm.Span);
            _remotePcmBytes += pcm.Length;
        }
        finally { _remoteFeedGate.Release(); }
    }

    async Task StartCoreAsync(AudioSource source, WhisperBackend backend, bool dictationMode,
        int remoteSampleRate = 0)
    {
        if (IsRunning || _captureLeaseHeld) return;

        PrepareDiagnosticLog();
        Log($"=== START source={source} (Vosk, раздельные конвейеры) ===");

        if (!Directory.Exists(ModelDir))
        {
            Status?.Invoke($"Не найдена модель Vosk: {ModelDir}");
            return;
        }

        if (dictationMode && source is not (AudioSource.Microphone or AudioSource.RemotePcm))
            throw new ArgumentException("Dictation mode requires microphone capture.", nameof(source));
        if (dictationMode && !File.Exists(WhisperModel))
            throw new FileNotFoundException("Для точной диктовки нужна локальная модель Whisper.", WhisperModel);

        if (source != AudioSource.RemotePcm) AcquireCaptureLease();

        _dictationMode = dictationMode;
        _dictationSamples = dictationMode ? new List<float>() : null;
        _dictationCancellation = dictationMode ? new CancellationTokenSource() : null;
        _dictationCancelled = false;
        _dictationChunkResults.Clear();
        _dictationTotalChunks = 0;

        try
        {
        // 1. Движок Vosk
        Status?.Invoke("Загрузка модели…");
        Vosk.Vosk.SetLogLevel(-1);
        _model = await Task.Run(() => new Model(ModelDir));
        _segId = 0;
        _micSegEnergy = _sysConcurEnergy = 0;

        // 1b. Корректор терминов (пост-замена кириллических расшифровок) + Whisper со словарём-подсказкой
        _corrector = TermCorrector.Load(Path.Combine(AppContext.BaseDirectory, "terms.txt"), Log);
        _whisper = new WhisperRefiner(Path.GetFullPath(WhisperModel), backend, "ru",
            LoadVocabulary(), Path.GetFullPath("silero_vad.onnx"),
            refinementTimeout: dictationMode ? TimeSpan.FromMinutes(3) : null,
            drainTimeout: dictationMode ? TimeSpan.FromMinutes(10) : null,
            timeoutIncludesQueueWait: !dictationMode);
        _whisper.SegmentRefined += (id, text) =>
        {
            var fixedText = _corrector?.Correct(text) ?? text;
            if (fixedText != text) Log($"термины #{id}: применена коррекция");
            if (_dictationMode && id < 0)
            {
                _dictationChunkResults[id] = fixedText;
                DictationProgress?.Invoke(_dictationChunkResults.Count, _dictationTotalChunks);
            }
            else
                SegmentRefined?.Invoke(id, fixedText);
        };
        _whisper.Log += Log;
        if (_whisper.ModelExists)
        {
            Status?.Invoke($"Загрузка Whisper ({backend})…");
            await _whisper.StartAsync();
            var onGpu = _whisper.LoadedBackend is "Cuda" or "Vulkan";
            Log($"whisper: модель загружена, backend={_whisper.LoadedBackend} (запрошен {backend})");
            if (backend != WhisperBackend.Cpu && !onGpu) Log("whisper: GPU недоступен — работаю на CPU");
        }
        else
        {
            _whisper = null;
            Log($"whisper: модель {WhisperModel} не найдена — только Vosk");
        }
        WhisperActive = _whisper is not null;

        // 1c. Диаризатор собеседников (опционально, если есть модель голосовых отпечатков)
        _diarizer = SpeakerDiarizer.TryCreate(Rate, Log);
        Log(_diarizer is null
            ? "диаризация: модель отпечатков не найдена — собеседники не делятся (все «Собеседник 1»)"
            : "диаризация: включена — собеседники делятся на Участник 1/2/3");

        // 2. Источники — каждый со своим распознавателем
        _sources.Clear();
        if (source is AudioSource.System or AudioSource.Both) AddSource(new WasapiLoopbackCapture(), isMic: false);
        if (source is AudioSource.Microphone or AudioSource.Both) AddSource(new WasapiCapture(), isMic: true);
        if (source == AudioSource.RemotePcm)
        {
            AddSource(new RemotePcmWaveIn(remoteSampleRate), isMic: true);
            _remotePcmSource = _sources[^1];
        }
        foreach (var s in _sources) s.Capture.StartRecording();

        // 2b. AEC: если есть и микрофон, и системный звук — вычитаем эхо колонок из микрофона
        bool both = _sources.Any(s => s.IsMic) && _sources.Any(s => !s.IsMic);
        _aec = both ? new EchoCanceller(AecFilterLen, PumpBlock) : null;
        Log(both ? "AEC: включён (вычитание системного звука из микрофона)" : "AEC: выключен (нужны оба источника)");

        // 3. Насос
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpLoop(_cts.Token));

        IsRunning = true;
        var srcText = source switch
        {
            AudioSource.System => "Слушаю системный звук",
            AudioSource.Microphone => "Слушаю микрофон",
            AudioSource.RemotePcm => "Принимаю звук с телефона",
            _ => "Слушаю микрофон + системный звук"
        };
        string waInfo;
        if (_whisper is null) waInfo = "Whisper выкл.";
        else
        {
            var onGpu = _whisper.LoadedBackend is "Cuda" or "Vulkan";
            waInfo = $"Whisper: {(onGpu ? "GPU" : "CPU")}";
            if (backend != WhisperBackend.Cpu && !onGpu) waInfo += " ⚠ GPU недоступен";
        }
        Status?.Invoke($"{srcText} · {waInfo}");
        }
        catch
        {
            await CleanupFailedStartAsync();
            ReleaseCaptureLease();
            throw;
        }
    }

    void PrepareDiagnosticLog()
    {
        if (_logPath.Length > 0) return;
        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localData)
            ? AppContext.BaseDirectory
            : Path.Combine(localData, "CodexVoice");
        var directory = Path.Combine(root, "logs");
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "codex-voice-asr.log");
        lock (LogFileSync)
            RotateDiagnosticLogIfNeeded();
    }

    void RotateDiagnosticLogIfNeeded()
    {
        if (_logPath.Length == 0
            || !File.Exists(_logPath)
            || new FileInfo(_logPath).Length < MaxLogBytes)
            return;

        var oldest = _logPath + "." + MaxLogArchives;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = MaxLogArchives - 1; index >= 1; index--)
        {
            var source = _logPath + "." + index;
            if (!File.Exists(source)) continue;
            File.Move(source, _logPath + "." + (index + 1));
        }
        File.Move(_logPath, _logPath + ".1");
    }

    void AcquireCaptureLease()
    {
        lock (CaptureLeaseSync)
        {
            if (_captureOwner is not null && !ReferenceEquals(_captureOwner, this))
                throw new InvalidOperationException(
                    "Другой модуль уже использует аудиозахват. Остановите текущую запись и повторите.");
            _captureOwner = this;
            _captureLeaseHeld = true;
        }
    }

    void ReleaseCaptureLease()
    {
        lock (CaptureLeaseSync)
        {
            if (!_captureLeaseHeld) return;
            _captureLeaseHeld = false;
            if (ReferenceEquals(_captureOwner, this))
                _captureOwner = null;
        }
    }

    async Task CleanupFailedStartAsync()
    {
        IsRunning = false;
        WhisperActive = false;
        try { _cts?.Cancel(); } catch { }
        if (_pump is not null)
        {
            try { await _pump; } catch { }
        }
        foreach (var source in _sources)
        {
            try { source.Capture.StopRecording(); } catch { }
            try { source.Capture.Dispose(); } catch { }
            try { source.Rec.Dispose(); } catch { }
        }
        try { _model?.Dispose(); } catch { }
        try { _diarizer?.Dispose(); } catch { }
        if (_whisper is not null)
        {
            try { await _whisper.StopAsync(); } catch { }
        }
        try { _cts?.Dispose(); } catch { }
        _sources.Clear();
        _model = null;
        _cts = null;
        _pump = null;
        _whisper = null;
        _diarizer = null;
        _aec = null;
        _corrector = null;
        _dictationSamples = null;
        _dictationChunkResults.Clear();
        _dictationTotalChunks = 0;
        _dictationMode = false;
        _dictationCancellation?.Dispose();
        _dictationCancellation = null;
        _dictationCancelled = false;
        _remotePcmSource = null;
        _remotePcmBytes = 0;
    }

    void AddSource(IWaveIn capture, bool isMic)
    {
        var fmt = capture.WaveFormat;
        var raw = new BufferedWaveProvider(fmt)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        ISampleProvider sp = raw.ToSampleProvider();
        if (sp.WaveFormat.Channels == 2)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sp.WaveFormat.SampleRate != Rate)
            sp = new WdlResamplingSampleProvider(sp, Rate);

        capture.DataAvailable += (_, e) => { try { raw.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { } };
        _sources.Add(new Src
        {
            Name = capture.GetType().Name,
            IsMic = isMic,
            Capture = capture,
            Raw = raw,
            Resampled = sp,
            Rec = new VoskRecognizer(_model!, Rate),
        });
        Log($"source: {capture.GetType().Name} ({(isMic ? "Я" : "собеседник")}), {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
    }

    async Task PumpLoop(CancellationToken ct)
    {
        var sources = _sources;
        var micSrc = sources.FirstOrDefault(s => s.IsMic);
        var sysSrc = sources.FirstOrDefault(s => !s.IsMic);
        bool hasSystem = sysSrc is not null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var pcm = new byte[PumpBlock * 2];
        int levelTick = 0, aliveTick = 0;
        float meterMax = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // фаза 1: читаем блок каждого источника
                foreach (var s in sources) s.BlockN = s.Resampled.Read(s.Block, 0, PumpBlock);

                // фаза 2: AEC — вычитаем системный звук (эхо) из микрофона
                if (_aec is not null && micSrc is not null && sysSrc is not null && micSrc.BlockN > 0)
                    _aec.Process(micSrc.Block, micSrc.BlockN, sysSrc.Block, sysSrc.BlockN);

                // фаза 3: распознавание по каждому источнику (микрофон — уже очищенный)
                float blockLevel = 0;
                foreach (var s in sources)
                {
                    int n = s.BlockN;
                    if (n <= 0) continue;
                    var buf = s.Block;

                    double ssum = 0;
                    for (int i = 0; i < n; i++)
                    {
                        // усиление — только для Vosk; в SegBuf (Whisper + диаризатор) кладём чистый звук
                        float g = Math.Clamp(buf[i] * Gain, -1f, 1f);
                        short v = (short)(g * 32767f);
                        pcm[2 * i] = (byte)(v & 0xff);
                        pcm[2 * i + 1] = (byte)((v >> 8) & 0xff);
                        ssum += buf[i] * buf[i];
                        if (s.SegBuf.Count < MaxSegSamples) s.SegBuf.Add(buf[i]);
                        if (s.IsMic) _dictationSamples?.Add(buf[i]);
                    }
                    float lvl = (float)Math.Sqrt(ssum / n);
                    if (lvl > s.MaxLevel) s.MaxLevel = lvl;
                    if (lvl > blockLevel) blockLevel = lvl;

                    // энергия для защиты от остаточного эха (микрофон — уже очищенный AEC)
                    if (s.IsMic) _micSegEnergy += ssum; else _sysConcurEnergy += ssum;

                    int bytes = n * 2;
                    if (s.Rec.AcceptWaveform(pcm, bytes))
                    {
                        var text = JsonText(s.Rec.Result(), "text");
                        // эхо чистит AEC, дубли ловит дедуп по тексту в UI — мик-речь больше НЕ глушим энергией
                        if (!string.IsNullOrWhiteSpace(text)) Emit(s, text, tail: false);
                        s.SegBuf.Clear();
                        s.LastPartial = "";
                        PartialResult?.Invoke(s.IsMic ? SpeakerMe : s.LastSpeakerKey, "");
                        if (s.IsMic) { _micSegEnergy = 0; _sysConcurEnergy = 0; }
                    }
                    else
                    {
                        var p = JsonText(s.Rec.PartialResult(), "partial");
                        if (p != s.LastPartial)
                        {
                            s.LastPartial = p;
                            var key = s.IsMic ? SpeakerMe : (s.LastSpeakerKey.Length > 0 ? s.LastSpeakerKey : "remote1");
                            PartialResult?.Invoke(key, p);   // живые слова — без тяжёлой диаризации
                        }
                        if (s.IsMic && p.Length == 0) { _micSegEnergy = 0; _sysConcurEnergy = 0; }   // микрофон молчит — сброс
                    }
                }

                if (blockLevel > meterMax) meterMax = blockLevel;
                if (++levelTick >= 4) { levelTick = 0; AudioLevel?.Invoke(meterMax); meterMax = 0; }

                if (++aliveTick >= 100)
                {
                    aliveTick = 0;
                    var per = string.Join(", ", sources.Select(s => $"{s.Name}={s.MaxLevel:F4}"));
                    Log($"alive: [{per}]");
                    foreach (var s in sources) s.MaxLevel = 0;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"PUMP ERROR: {ex}"); Status?.Invoke("Ошибка: " + ex.Message); }
    }

    /// <summary>Ключ говорящего (тяжёлая диаризация считается ТОЛЬКО здесь — в конце фразы).</summary>
    string SpeakerOf(Src s)
    {
        var key = s.IsMic ? SpeakerMe : (_diarizer?.Identify(s.SegBuf) ?? "remote1");
        s.LastSpeakerKey = key;
        return key;
    }

    void Emit(Src s, string text, bool tail)
    {
        var id = ++_segId;
        var who = SpeakerOf(s);
        var time = DateTime.Now;
        // Собеседника уточняем всегда. Ваш голос — Whisper'ом ТОЛЬКО без наложения системы
        // (иначе он подмешивает куски записи/эха). При наложении оставляем черновик Vosk.
        bool refine = _whisper is not null
                      && (_dictationMode || !s.IsMic || _sysConcurEnergy < _micSegEnergy * 0.5);
        Log(
            $"final #{id} [{who}]{(tail ? " (хвост)" : "")}: "
            + $"chars={text.Length}, refine={refine}");
        SegmentFinalized?.Invoke(id, time, text, who, refine);
        SegmentRecognized?.Invoke(id, time, text, who);
        if (refine) _whisper!.Enqueue(id, NormalizeForWhisper(s.SegBuf));
    }

    // Whisper хуже распознаёт тихий звук — нормализуем громкость сегмента к пику ~0.95 (без клиппинга).
    // Тихий шум/тишину не усиливаем (иначе Whisper начнёт галлюцинировать).
    static float[] NormalizeForWhisper(IReadOnlyList<float> buf)
    {
        var arr = new float[buf.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = buf[i];
        float max = 0;
        foreach (var v in arr) { var a = Math.Abs(v); if (a > max) max = a; }
        if (max > 0.015f)
        {
            float g = 0.95f / max;
            for (int i = 0; i < arr.Length; i++) arr[i] *= g;
        }
        return arr;
    }

    const string VocabFile = "vocabulary.txt";

    // Словарь-подсказка для Whisper: правильное написание имён и IT-жаргона (Grafana, Zabbix…).
    // Берётся из vocabulary.txt рядом с приложением; если файла нет — создаётся с дефолтом.
    string LoadVocabulary()
    {
        const int MaxPromptChars = 350;   // длинная подсказка ломает Whisper (выдаёт «…») — держим короткой
        var path = Path.Combine(AppContext.BaseDirectory, VocabFile);
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, DefaultVocabulary);
            var text = File.ReadAllText(path).Trim();
            if (text.Length > MaxPromptChars)
            {
                text = text[..MaxPromptChars];
                Log($"словарь Whisper: обрезан до {MaxPromptChars} симв. (ограничение подсказки)");
            }
            Log($"словарь Whisper: {(text.Length > 0 ? $"{text.Length} симв." : "пусто")}");
            return text;
        }
        catch (Exception ex) { Log($"словарь Whisper: ошибка чтения ({ex.Message})"); return ""; }
    }

    // Курируемый словарь IT/DevOps/1С — правильное написание имён и жаргона в подсказке Whisper.
    // Можно редактировать в vocabulary.txt рядом с приложением (держите в пределах ~200 слов).
    const string DefaultVocabulary =
        "Разговор про DevOps и 1С. Термины: Kubernetes, Docker, Jenkins, GitLab, SonarQube, PostgreSQL, " +
        "Grafana, Zabbix, Prometheus, Ansible, Terraform, Helm, Nginx, playbook, namespace, " +
        "vrunner, oscript, ibcmd, Vanessa Automation, Обновлятор 1С.";

    // Keep the complete microphone recording independent of Vosk endpoints.
    // Whisper receives no more than 30 seconds per job; nearby quiet frames
    // place longer dictation boundaries between words when possible.
    static List<float[]> BuildDictationChunks(List<float> samples)
    {
        const int target = Rate * 25;
        const int search = Rate * 2;
        const int overlap = Rate / 2;
        const int frame = PumpBlock;
        var chunks = new List<float[]>();
        var start = 0;
        while (start < samples.Count)
        {
            var available = samples.Count - start;
            var maxContent = MaxSegSamples - (start == 0 ? 0 : overlap);
            var end = available <= maxContent
                ? samples.Count
                : FindQuietBoundary(samples, start + target, search, frame);
            var copyStart = start == 0 ? 0 : Math.Max(0, start - overlap);
            var copyEnd = end == samples.Count ? end : Math.Min(samples.Count, end + overlap);
            var chunk = new float[copyEnd - copyStart];
            samples.CopyTo(copyStart, chunk, 0, chunk.Length);
            chunks.Add(NormalizeForWhisper(chunk));
            start = end;
        }
        return chunks;
    }

    static int FindQuietBoundary(List<float> samples, int target, int radius, int frame)
    {
        var first = Math.Max(frame, target - radius);
        var last = Math.Min(samples.Count - frame, target + radius);
        var best = target;
        double bestEnergy = double.MaxValue;
        for (int offset = first; offset <= last; offset += frame)
        {
            double energy = 0;
            for (int i = offset; i < offset + frame; i++)
                energy += samples[i] * samples[i];
            if (energy >= bestEnergy) continue;
            bestEnergy = energy;
            best = offset;
        }
        return best;
    }

    static string JoinDictationChunks(IEnumerable<string> chunks)
    {
        var words = new List<string>();
        foreach (var chunk in chunks)
        {
            var next = chunk.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int overlap = 0;
            for (int n = Math.Min(12, Math.Min(words.Count, next.Length)); n > 0; n--)
            {
                if (!Enumerable.Range(0, n).All(i =>
                        CanonicalWord(words[words.Count - n + i]) == CanonicalWord(next[i])))
                    continue;
                overlap = n;
                break;
            }
            words.AddRange(next.Skip(overlap));
        }
        return string.Join(" ", words);
    }

    static string CanonicalWord(string word) =>
        new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    void DrainDictationSource(Src source)
    {
        if (_dictationSamples is null) return;
        var pcm = new byte[PumpBlock * 2];
        int n;
        while ((n = source.Resampled.Read(source.Block, 0, PumpBlock)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var sample = source.Block[i];
                _dictationSamples.Add(sample);
                if (source.SegBuf.Count < MaxSegSamples) source.SegBuf.Add(sample);
                var value = (short)(Math.Clamp(sample * Gain, -1f, 1f) * 32767f);
                pcm[2 * i] = (byte)value;
                pcm[2 * i + 1] = (byte)(value >> 8);
            }
            if (source.Rec.AcceptWaveform(pcm, n * 2))
            {
                var text = JsonText(source.Rec.Result(), "text");
                if (!string.IsNullOrWhiteSpace(text)) Emit(source, text, tail: false);
                source.SegBuf.Clear();
            }
        }
    }

    static string JsonText(string json, string field)
    {
        try { return JsonDocument.Parse(json).RootElement.GetProperty(field).GetString() ?? ""; }
        catch { return ""; }
    }

    public void CancelDictation()
    {
        if (!_dictationMode) return;
        _dictationCancelled = true;
        try { _dictationCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        _whisper?.CancelPending();
    }

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
        if (!IsRunning)
        {
            ReleaseCaptureLease();
            return;
        }
        IsRunning = false;
        Log("stop: запрошена остановка");

        _cts?.Cancel();
        Exception? dictationFailure = null;
        var dictationChunkIds = new List<long>();

        try
        {
            if (_pump is not null)
            {
                try { await _pump; }
                catch (Exception exception) { Log($"pump stop error: {exception.Message}"); }
            }
            foreach (var s in _sources)
            {
                try { s.Capture.StopRecording(); }
                catch (Exception exception) { Log($"capture stop error: {exception.Message}"); }
                if (_dictationMode && !_dictationCancelled && s.IsMic)
                {
                    try { DrainDictationSource(s); }
                    catch (Exception exception) { dictationFailure ??= exception; }
                }
                try { s.Capture.Dispose(); }
                catch (Exception exception) { Log($"capture dispose error: {exception.Message}"); }
                try
                {
                    var tail = JsonText(s.Rec.FinalResult(), "text");
                    if (!string.IsNullOrWhiteSpace(tail)) Emit(s, tail, tail: true);
                }
                catch (Exception exception) { Log($"tail finalize error: {exception.Message}"); }
                try { s.Rec.Dispose(); }
                catch (Exception exception) { Log($"recognizer dispose error: {exception.Message}"); }
            }
            try { _model?.Dispose(); }
            catch (Exception exception) { Log($"model dispose error: {exception.Message}"); }
            try { _diarizer?.Dispose(); }
            catch (Exception exception) { Log($"diarizer dispose error: {exception.Message}"); }
            if (_dictationMode && !_dictationCancelled && dictationFailure is null
                && _whisper is not null && _dictationSamples is not null)
            {
                try
                {
                    var chunks = BuildDictationChunks(_dictationSamples);
                    _dictationTotalChunks = chunks.Count;
                    if (chunks.Count > 0) DictationProgress?.Invoke(0, chunks.Count);
                    using var enqueueTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        _dictationCancellation?.Token ?? CancellationToken.None);
                    enqueueTimeout.CancelAfter(TimeSpan.FromMinutes(10));
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        var id = -(i + 1L);
                        await _whisper.EnqueueAsync(id, chunks[i], enqueueTimeout.Token);
                        dictationChunkIds.Add(id);
                    }
                    Log($"dictation: queued {dictationChunkIds.Count} Whisper chunk(s)");
                }
                catch (OperationCanceledException) when (_dictationCancelled) { }
                catch (Exception exception) { dictationFailure = exception; }
            }
            if (_whisper is not null)
            {
                try { await _whisper.StopAsync(); }
                catch (Exception exception)
                {
                    Log($"whisper stop error: {exception.Message}");
                    if (_dictationMode && !_dictationCancelled) dictationFailure ??= exception;
                }
                if (_dictationMode && !_dictationCancelled && !_whisper.DrainCompleted)
                    dictationFailure ??= new TimeoutException("Whisper не завершил локальное распознавание. Текст не отправлен.");
            }
            if (_dictationMode && !_dictationCancelled && dictationFailure is null
                && dictationChunkIds.Count > 0)
            {
                if (dictationChunkIds.Any(id => !_dictationChunkResults.ContainsKey(id)))
                    dictationFailure = new InvalidOperationException("Whisper не вернул все части диктовки. Текст не отправлен.");
                else if (dictationChunkIds.All(id => !string.IsNullOrWhiteSpace(_dictationChunkResults[id])))
                    DictationRefined?.Invoke(JoinDictationChunks(
                        dictationChunkIds.Select(id => _dictationChunkResults[id])));
                else
                    Status?.Invoke("Whisper не разобрал короткую фразу; сохранён черновик Vosk для проверки.");
            }
            try { _cts?.Dispose(); }
            catch (Exception exception) { Log($"stop token dispose error: {exception.Message}"); }
            Log("stop: остановлено");
        }
        finally
        {
            _sources.Clear();
            _model = null;
            _cts = null;
            _pump = null;
            _whisper = null;
            _diarizer = null;
            _aec = null;
            _corrector = null;
            _dictationSamples = null;
            _dictationChunkResults.Clear();
            _dictationTotalChunks = 0;
            _dictationMode = false;
            _dictationCancellation?.Dispose();
            _dictationCancellation = null;
            _dictationCancelled = false;
            _remotePcmSource = null;
            _remotePcmBytes = 0;
            WhisperActive = false;
            ReleaseCaptureLease();
        }

        Status?.Invoke("Остановлено.");
        AudioLevel?.Invoke(0);
        if (dictationFailure is not null) throw dictationFailure;
    }
}
