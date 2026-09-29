using LiveTranscribeRu;

namespace CodexVoice;

internal sealed class SpeechCapture
{
    private readonly TranscriptionService _service = new();
    private readonly TranscriptBuffer _buffer = new();
    private volatile bool _cancelRequested;
    private Exception? _finalizationFailure;

    public event Action<string>? TextChanged;
    public event Action<float>? LevelChanged;
    public event Action<string>? StatusChanged;
    public event Action<int, int>? FinalizationProgress;

    public SpeechCapture()
    {
        _service.PartialResult += (_, text) => { _buffer.SetPartial(text); TextChanged?.Invoke(_buffer.Preview); };
        _service.SegmentRecognized += (id, _, text, _) => { _buffer.Add(id, text); TextChanged?.Invoke(_buffer.Preview); };
        _service.SegmentRefined += (id, text) => { _buffer.Refine(id, text); TextChanged?.Invoke(_buffer.Preview); };
        _service.DictationRefined += text => { _buffer.SetFinalOverride(text); TextChanged?.Invoke(_buffer.Final); };
        _service.AudioLevel += level => LevelChanged?.Invoke(level);
        _service.Status += status => StatusChanged?.Invoke(status);
        _service.DictationProgress += (completed, total) => FinalizationProgress?.Invoke(completed, total);
    }

    public async Task StartAsync()
    {
        _cancelRequested = false;
        _finalizationFailure = null;
        _buffer.Clear();
        TextChanged?.Invoke("");
        await _service.StartAsync(AudioSource.Microphone, WhisperBackend.Auto, dictationMode: true);
        if (!_service.IsRunning) throw new InvalidOperationException("Микрофон не запущен. Проверьте модель Vosk и разрешение на запись.");
        if (!_service.WhisperActive) throw new InvalidOperationException("Локальная модель Whisper не запущена.");
    }

    public async Task<string> FinishAsync()
    {
        if (_finalizationFailure is not null)
            throw new InvalidOperationException("Локальное уточнение не завершилось; начните новую диктовку.", _finalizationFailure);
        try { await _service.StopAsync(); }
        catch (Exception exception)
        {
            _finalizationFailure = exception;
            throw;
        }
        if (_cancelRequested) throw new OperationCanceledException("Диктовка отменена.");
        return _buffer.Final;
    }

    public void RequestCancel()
    {
        _cancelRequested = true;
        _service.CancelDictation();
    }

    public async Task CancelAsync()
    {
        RequestCancel();
        try { await _service.StopAsync(); }
        finally { _buffer.Clear(); }
    }
}
