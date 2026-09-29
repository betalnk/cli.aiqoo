using Avalonia.Threading;

namespace CodexVoice;

/// <summary>
/// Owns answer navigation and local speech for exactly one selected CLI thread.
/// The App owns this controller, so auto-read continues after the overlay closes.
/// </summary>
internal sealed class SessionPlaybackController : IDisposable
{
    // SAPI accepts 16,000 characters per call. Smaller chunks let interruption
    // and microphone capture take effect without waiting through a long answer.
    private const int SpeechChunkLength = 8_000;

    private readonly object _gate = new();
    private readonly CodexAnswerMonitor _monitor = new();
    private readonly LocalSpeechPlayer _player = new();
    private readonly Queue<CodexAnswer> _waitingAnswers = new();
    private string? _selectedThreadId;
    private PlaybackPlan? _plan;
    private long _activeRequestId;
    private int _selectionVersion;
    private bool _ready;
    private bool _autoRead;
    private bool _microphoneActive;
    private bool _disposed;

    internal event Action? StateChanged;
    internal event Action<string>? Error;

    internal SessionPlaybackController()
    {
        _monitor.NewAnswer += OnNewAnswer;
        _monitor.ReadFailed += OnReadFailed;
        _player.Completed += OnSpeechCompleted;
        _player.Failed += OnSpeechFailed;
    }

    internal string? SelectedThreadId
    {
        get { lock (_gate) return _selectedThreadId; }
    }

    internal bool IsReady
    {
        get { lock (_gate) return _ready; }
    }

    internal bool IsPlaying
    {
        get { lock (_gate) return _activeRequestId != 0; }
    }

    internal bool MicrophoneActive
    {
        get { lock (_gate) return _microphoneActive; }
    }

    internal CodexAnswer? Current => _monitor.Current;

    /// <summary>
    /// Auto-read is bound to the selected thread. Selecting a different thread
    /// switches it off, preventing speech from following the wrong session.
    /// </summary>
    internal bool AutoRead
    {
        get { lock (_gate) return _autoRead; }
        set
        {
            string? error = null;
            bool changed;
            lock (_gate)
            {
                if (_disposed) return;
                if (value && !_ready)
                {
                    error = "Сначала дождитесь загрузки истории выбранной сессии.";
                    changed = false;
                }
                else
                {
                    changed = _autoRead != value;
                    _autoRead = value;
                    if (!value)
                    {
                        _waitingAnswers.Clear();
                        if (_plan is { Automatic: true })
                        {
                            _plan = null;
                            _activeRequestId = 0;
                            _player.Stop();
                        }
                    }
                }
            }
            if (error is not null) RaiseError(error);
            if (changed) RaiseStateChanged();
        }
    }

    /// <summary>
    /// Snapshot the selected thread before sending a prompt. Old answers remain
    /// browsable, but are never spoken when auto-read is enabled afterward.
    /// </summary>
    internal async Task SetSelectedSessionAsync(
        string threadId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(threadId, out var parsed))
            throw new ArgumentException("Ожидался UUID сессии Codex.", nameof(threadId));
        var canonical = parsed.ToString("D");
        int version;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_selectedThreadId == canonical && _ready) return;
            var changed = _selectedThreadId != canonical;
            _selectedThreadId = canonical;
            _ready = false;
            version = ++_selectionVersion;
            if (changed)
            {
                _autoRead = false;
                _waitingAnswers.Clear();
                _plan = null;
                _activeRequestId = 0;
                _player.Stop();
            }
            _monitor.Stop();
        }
        RaiseStateChanged();

        try
        {
            await _monitor.StartAsync(canonical, cancellationToken);
        }
        catch (Exception exception)
        {
            bool current;
            lock (_gate)
            {
                current = !_disposed && _selectionVersion == version;
                if (current)
                {
                    _ready = false;
                    _autoRead = false;
                }
            }
            if (!current) return;
            RaiseStateChanged();
            RaiseError("Не удалось прочитать историю сессии: " + exception.Message);
            throw;
        }

        lock (_gate)
        {
            if (_disposed || _selectionVersion != version) return;
            _ready = true;
        }
        RaiseStateChanged();
    }

    /// <summary>Replay the answer currently selected in the answer history.</summary>
    internal Task<CodexAnswer?> PlayCurrentAsync() =>
        Task.FromResult(PlayAnswer(_monitor.Current));

    /// <summary>Move one answer back or forward and speak the selected answer.</summary>
    internal async Task<CodexAnswer?> MoveAndPlayAsync(
        int offset, CancellationToken cancellationToken = default)
    {
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));
        string? selected;
        lock (_gate)
        {
            if (_disposed || !_ready) return null;
            selected = _selectedThreadId;
        }
        try
        {
            var answer = await _monitor.MoveAsync(offset, cancellationToken);
            if (answer is null || answer.ThreadId != selected) return null;
            return PlayAnswer(answer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            RaiseError("Не удалось открыть ответ: " + exception.Message);
            return null;
        }
    }

    /// <summary>Stop the current answer without disabling future auto-read.</summary>
    internal void StopPlayback()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _plan = null;
            _activeRequestId = 0;
            _waitingAnswers.Clear();
            _player.Stop();
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// Interrupt speech before capture begins. The interrupted chunk is replayed
    /// after capture, so SAPI audio cannot be transcribed from the microphone.
    /// </summary>
    internal void StopForMicrophone()
    {
        lock (_gate)
        {
            if (_disposed || _microphoneActive) return;
            _microphoneActive = true;
            if (_activeRequestId != 0 && _plan is not null)
                _plan.NextChunk = Math.Max(0, _plan.NextChunk - 1);
            _activeRequestId = 0;
            _player.Stop();
        }
        RaiseStateChanged();
    }

    internal void ResumeAfterMicrophone()
    {
        Exception? error;
        lock (_gate)
        {
            if (_disposed || !_microphoneActive) return;
            _microphoneActive = false;
            error = AdvancePlaybackLocked();
        }
        if (error is not null) RaiseError("Не удалось озвучить ответ: " + error.Message);
        RaiseStateChanged();
    }

    /// <summary>
    /// Called after the overlay closes. Polling stops if auto-read is off; when
    /// auto-read is on, the exact thread keeps being watched in the background.
    /// </summary>
    internal void StopIdleMonitoring()
    {
        lock (_gate)
        {
            if (_disposed || _autoRead || _selectedThreadId is null) return;
            // Also cancel an initial snapshot still in flight when the overlay
            // closes before history loading has finished.
            _selectionVersion++;
            _monitor.Stop();
            _ready = false;
            _waitingAnswers.Clear();
        }
        RaiseStateChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _selectionVersion++;
            _autoRead = false;
            _ready = false;
            _waitingAnswers.Clear();
            _plan = null;
            _activeRequestId = 0;
        }
        _monitor.NewAnswer -= OnNewAnswer;
        _monitor.ReadFailed -= OnReadFailed;
        _player.Completed -= OnSpeechCompleted;
        _player.Failed -= OnSpeechFailed;
        _monitor.Dispose();
        _player.Dispose();
    }

    private CodexAnswer? PlayAnswer(CodexAnswer? answer)
    {
        if (answer is null) return null;
        Exception? error;
        lock (_gate)
        {
            if (_disposed || !_ready || answer.ThreadId != _selectedThreadId) return null;
            _waitingAnswers.Clear();
            _plan = new PlaybackPlan(answer, automatic: false);
            _activeRequestId = 0;
            if (_microphoneActive) _player.Stop();
            error = AdvancePlaybackLocked();
        }
        if (error is not null) RaiseError("Не удалось озвучить ответ: " + error.Message);
        RaiseStateChanged();
        return answer;
    }

    private void OnNewAnswer(CodexAnswer answer)
    {
        Exception? error = null;
        lock (_gate)
        {
            if (_disposed || answer.ThreadId != _selectedThreadId) return;
            if (_autoRead)
            {
                _waitingAnswers.Enqueue(answer);
                if (_plan is null && !_microphoneActive) error = AdvancePlaybackLocked();
            }
        }
        if (error is not null) RaiseError("Не удалось озвучить ответ: " + error.Message);
        RaiseStateChanged();
    }

    private void OnReadFailed(string message) =>
        RaiseError("Не удалось проверить новые ответы: " + message);

    private void OnSpeechCompleted(long requestId)
    {
        Exception? error;
        lock (_gate)
        {
            if (_disposed || requestId != _activeRequestId) return;
            _activeRequestId = 0;
            error = AdvancePlaybackLocked();
        }
        if (error is not null) RaiseError("Не удалось озвучить ответ: " + error.Message);
        RaiseStateChanged();
    }

    private void OnSpeechFailed(long requestId, Exception exception)
    {
        lock (_gate)
        {
            if (_disposed || requestId != _activeRequestId) return;
            _activeRequestId = 0;
            _plan = null;
            _waitingAnswers.Clear();
            _autoRead = false;
        }
        RaiseError("Не удалось озвучить ответ: " + exception.Message);
        RaiseStateChanged();
    }

    /// <summary>Caller holds _gate. Returns a failure to report after unlocking.</summary>
    private Exception? AdvancePlaybackLocked()
    {
        while (!_disposed && !_microphoneActive)
        {
            if (_plan is null)
            {
                if (_waitingAnswers.Count == 0) return null;
                _plan = new PlaybackPlan(_waitingAnswers.Dequeue(), automatic: true);
            }
            if (_plan.NextChunk >= _plan.Chunks.Count)
            {
                _plan = null;
                continue;
            }
            try
            {
                _activeRequestId = _player.Speak(_plan.Chunks[_plan.NextChunk]);
                _plan.NextChunk++;
                return null;
            }
            catch (Exception exception)
            {
                _activeRequestId = 0;
                _plan = null;
                _waitingAnswers.Clear();
                _autoRead = false;
                return exception;
            }
        }
        return null;
    }

    /// <summary>Split all received text at natural boundaries without dropping words.</summary>
    internal static IReadOnlyList<string> SplitText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + SpeechChunkLength, text.Length);
            if (end < text.Length)
            {
                var minimum = start + SpeechChunkLength / 2;
                end = FindBoundary(text, minimum, end, c => c is '\n' or '\r')
                    ?? FindSentenceBoundary(text, minimum, end)
                    ?? FindBoundary(text, minimum, end, char.IsWhiteSpace)
                    ?? end;
                if (char.IsHighSurrogate(text[end - 1])) end--;
            }
            var chunk = text[start..end];
            if (!string.IsNullOrWhiteSpace(chunk)) chunks.Add(chunk);
            start = end;
        }
        return chunks;
    }

    private static int? FindBoundary(string text, int minimum, int end, Func<char, bool> match)
    {
        for (var index = end - 1; index >= minimum; index--)
            if (match(text[index])) return index + 1;
        return null;
    }

    private static int? FindSentenceBoundary(string text, int minimum, int end)
    {
        for (var index = end - 1; index >= minimum; index--)
        {
            if (text[index] is not ('.' or '!' or '?' or '…')) continue;
            if (index + 1 < text.Length && char.IsWhiteSpace(text[index + 1])) return index + 1;
        }
        return null;
    }

    private void RaiseStateChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            try { StateChanged?.Invoke(); }
            catch { /* A view callback must not stop monitoring or speech. */ }
        }
        else Dispatcher.UIThread.Post(() =>
        {
            try { StateChanged?.Invoke(); }
            catch { /* The overlay may have closed since this event was queued. */ }
        });
    }

    private void RaiseError(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            try { Error?.Invoke(message); }
            catch { /* A view callback must not stop monitoring or speech. */ }
        }
        else Dispatcher.UIThread.Post(() =>
        {
            try { Error?.Invoke(message); }
            catch { /* The overlay may have closed since this event was queued. */ }
        });
    }

    private sealed class PlaybackPlan
    {
        internal PlaybackPlan(CodexAnswer answer, bool automatic)
        {
            Answer = answer;
            Automatic = automatic;
            Chunks = SplitText(answer.Text);
        }

        internal CodexAnswer Answer { get; }
        internal bool Automatic { get; }
        internal IReadOnlyList<string> Chunks { get; }
        internal int NextChunk { get; set; }
    }
}
