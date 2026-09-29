using System.Runtime.InteropServices;

namespace CodexVoice;

/// <summary>
/// Plays local SAPI speech on a dedicated STA thread. Each Speak replaces the
/// previous utterance; completion events are raised on that worker thread.
/// </summary>
internal sealed class LocalSpeechPlayer : IDisposable
{
    internal const int MaxTextLength = 16_000;

    private const int Async = 1;
    private const int PurgeBeforeSpeak = 2;
    private const int IsNotXml = 16;
    private const int SpeakFlags = Async | PurgeBeforeSpeak | IsNotXml;
    private const int CompletionPollMilliseconds = 40;

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private PlaybackRequest? _pending;
    private Exception? _unavailable;
    private long _nextId;
    private bool _disposed;

    /// <summary>Raised only when an utterance finishes naturally, on the STA worker.</summary>
    internal event Action<long>? Completed;

    /// <summary>Raised on the STA worker if a queued utterance cannot play.</summary>
    internal event Action<long, Exception>? Failed;

    internal LocalSpeechPlayer()
    {
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "CodexVoice local speech"
        };
        _worker.SetApartmentState(ApartmentState.STA);
        try { _worker.Start(); }
        catch
        {
            _wake.Dispose();
            throw;
        }
    }

    /// <summary>Queues text without waiting for audio. Returns the request ID used by events.</summary>
    internal long Speak(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaxTextLength)
            throw new ArgumentOutOfRangeException(nameof(text),
                $"Ответ для озвучивания длиннее {MaxTextLength} символов.");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_unavailable is not null)
                throw new InvalidOperationException("Локальный русский голос недоступен.", _unavailable);

            var id = ++_nextId;
            _pending = new PlaybackRequest(id, text);
            WakeWorker();
            return id;
        }
    }

    /// <summary>Discards pending text and interrupts current audio without blocking.</summary>
    internal void Stop()
    {
        lock (_gate)
        {
            if (_disposed || _unavailable is not null) return;
            _pending = new PlaybackRequest(0, null);
            WakeWorker();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
            WakeWorker();
        }

        if (Thread.CurrentThread != _worker)
        {
            // SAPI polls in short slices, so normal shutdown should finish promptly.
            _worker.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void Run()
    {
        object? voiceObject = null;
        long activeId = 0;
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice")
                ?? throw new InvalidOperationException("Windows SAPI не установлен.");
            voiceObject = Activator.CreateInstance(voiceType)
                ?? throw new InvalidOperationException("Не удалось создать голос Windows SAPI.");
            dynamic voice = voiceObject;
            SelectRussianVoice(voice);

            while (true)
            {
                PlaybackRequest? request;
                lock (_gate)
                {
                    if (_disposed) break;
                    request = _pending;
                    _pending = null;
                }

                if (request is not null)
                {
                    var interruptedId = activeId;
                    activeId = 0;
                    try
                    {
                        // Empty text plus PurgeBeforeSpeak stops audio without a new utterance.
                        voice.Speak(request.Text ?? "", SpeakFlags);
                        if (request.Text is not null) activeId = request.Id;
                    }
                    catch (Exception exception)
                    {
                        if (request.Id != 0) RaiseFailed(request.Id, exception);
                        else if (interruptedId != 0) RaiseFailed(interruptedId, exception);
                    }
                    continue;
                }

                if (activeId == 0)
                {
                    _wake.WaitOne();
                    continue;
                }

                bool finished;
                try { finished = voice.WaitUntilDone(CompletionPollMilliseconds); }
                catch (Exception exception)
                {
                    RaiseFailed(activeId, exception);
                    activeId = 0;
                    continue;
                }

                if (!finished) continue;
                bool notifyCompleted;
                lock (_gate)
                {
                    // A newer request or Stop takes precedence over a late completion.
                    notifyCompleted = !_disposed && _pending is null;
                }
                if (notifyCompleted) RaiseCompleted(activeId);
                activeId = 0;
            }
        }
        catch (Exception exception)
        {
            PlaybackRequest? pending;
            lock (_gate)
            {
                _unavailable = exception;
                pending = _pending;
                _pending = null;
            }
            if (activeId != 0) RaiseFailed(activeId, exception);
            if (pending is { Id: > 0 }) RaiseFailed(pending.Id, exception);
        }
        finally
        {
            if (voiceObject is not null)
            {
                try { ((dynamic)voiceObject).Speak("", SpeakFlags); }
                catch { /* Best effort when the audio device has failed. */ }
                ReleaseComObject(voiceObject);
            }
            _wake.Dispose();
        }
    }

    private static void SelectRussianVoice(dynamic voice)
    {
        object? voicesObject = null;
        object? chosenObject = null;
        try
        {
            voicesObject = voice.GetVoices("Language=419", "");
            dynamic voices = voicesObject;
            var count = (int)voices.Count;
            if (count == 0)
                throw new InvalidOperationException("Русский голос SAPI не установлен в Windows.");

            // Prefer the locally installed Irina voice when several Russian voices exist.
            for (var index = 0; index < count; index++)
            {
                object tokenObject = voices.Item(index);
                dynamic token = tokenObject;
                var description = (string)token.GetDescription();
                if (chosenObject is null || description.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                    description.Contains("Ирина", StringComparison.OrdinalIgnoreCase))
                {
                    ReleaseComObject(chosenObject);
                    chosenObject = tokenObject;
                    if (description.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                        description.Contains("Ирина", StringComparison.OrdinalIgnoreCase)) break;
                }
                else ReleaseComObject(tokenObject);
            }

            voice.Voice = chosenObject;
        }
        finally
        {
            ReleaseComObject(chosenObject);
            ReleaseComObject(voicesObject);
        }
    }

    private void RaiseCompleted(long id)
    {
        try { Completed?.Invoke(id); }
        catch { /* A UI callback must not terminate the speech thread. */ }
    }

    private void RaiseFailed(long id, Exception exception)
    {
        try { Failed?.Invoke(id, exception); }
        catch { /* A UI callback must not terminate the speech thread. */ }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); }
        catch { /* COM may already have disconnected after a SAPI failure. */ }
    }

    private void WakeWorker()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { /* Worker has already exited. */ }
    }

    private sealed record PlaybackRequest(long Id, string? Text);
}
