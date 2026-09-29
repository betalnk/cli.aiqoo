namespace CodexVoice;

internal sealed record CodexAnswer(string ThreadId, string TurnId, string ItemId, string Text)
{
    internal string Key => TurnId + "/" + ItemId;
}

/// <summary>
/// Reads only final answers from one selected Codex CLI thread. It snapshots existing
/// answers before polling, so enabling auto-read never speaks the old conversation.
/// </summary>
internal sealed class CodexAnswerMonitor : IDisposable
{
    private const int PageSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
    private readonly object _gate = new();
    private readonly Func<string, int, string?, CancellationToken, Task<CodexHistoryPage>> _read;
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly List<CodexAnswer> _answers = [];
    private CancellationTokenSource? _source;
    private string? _threadId;
    private string? _olderCursor;
    private int _position = -1;

    internal event Action<CodexAnswer>? NewAnswer;
    internal event Action<string>? ReadFailed;

    internal CodexAnswerMonitor(
        Func<string, int, string?, CancellationToken, Task<CodexHistoryPage>>? read = null)
    {
        _read = read ?? CodexHistoryReader.ReadPageAsync;
    }

    internal string? ThreadId
    {
        get { lock (_gate) return _threadId; }
    }

    internal CodexAnswer? Current
    {
        get
        {
            lock (_gate) return _position >= 0 && _position < _answers.Count
                ? _answers[_position] : null;
        }
    }

    internal async Task StartAsync(string threadId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(threadId, out var id))
            throw new ArgumentException("Ожидался UUID сессии Codex.", nameof(threadId));
        var canonical = id.ToString("D");
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            StopLocked();
            _source = source;
            _threadId = canonical;
            _known.Clear();
            _answers.Clear();
            _position = -1;
            _olderCursor = null;
        }

        try
        {
            var page = await _read(canonical, PageSize, null, source.Token);
            lock (_gate)
            {
                if (!ReferenceEquals(_source, source)) return;
                AddPageLocked(canonical, page, older: false);
                _position = _answers.Count - 1;
            }
            _ = PollAsync(canonical, source);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_source, source)) StopLocked();
            }
            throw;
        }
    }

    internal void Stop()
    {
        lock (_gate) StopLocked();
    }

    internal async Task<CodexAnswer?> MoveAsync(int offset, CancellationToken cancellationToken = default)
    {
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));
        string? threadId;
        string? cursor;
        CancellationToken sourceToken;
        lock (_gate)
        {
            if (_threadId is null || _source is null) return null;
            var next = _position + offset;
            if (next >= 0 && next < _answers.Count)
            {
                _position = next;
                return _answers[next];
            }
            if (offset > 0 || _olderCursor is null) return null;
            threadId = _threadId;
            cursor = _olderCursor;
            sourceToken = _source.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sourceToken, cancellationToken);
        var page = await _read(threadId, PageSize, cursor, linked.Token);
        lock (_gate)
        {
            if (_threadId != threadId || _source is null || _source.IsCancellationRequested) return null;
            var added = AddPageLocked(threadId, page, older: true);
            _position += added.Count;
            if (_position <= 0 || _position >= _answers.Count) return null;
            _position--;
            return _answers[_position];
        }
    }

    private async Task PollAsync(string threadId, CancellationTokenSource source)
    {
        var token = source.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, token);
                var page = await _read(threadId, PageSize, null, token);
                IReadOnlyList<CodexAnswer> fresh;
                lock (_gate)
                {
                    if (!ReferenceEquals(_source, source)) return;
                    fresh = AddPageLocked(threadId, page, older: false).NewAnswers;
                    if (fresh.Count > 0) _position = _answers.Count - 1;
                }
                foreach (var answer in fresh)
                {
                    if (token.IsCancellationRequested) break;
                    NewAnswer?.Invoke(answer);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                if (token.IsCancellationRequested) return;
                ReadFailed?.Invoke(exception.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private (int Count, IReadOnlyList<CodexAnswer> NewAnswers) AddPageLocked(
        string threadId, CodexHistoryPage page, bool older)
    {
        var found = new List<CodexAnswer>();
        foreach (var turn in page.Turns.Reverse())
        {
            if (!string.Equals(turn.Status, "completed", StringComparison.OrdinalIgnoreCase)) continue;
            var index = 0;
            foreach (var message in turn.Messages)
            {
                if (message.Role != "assistant" || string.IsNullOrWhiteSpace(message.Text)) continue;
                var itemId = string.IsNullOrEmpty(message.ItemId) ? $"assistant-{index}" : message.ItemId;
                var answer = new CodexAnswer(threadId, turn.TurnId, itemId, message.Text);
                if (_known.Add(answer.Key)) found.Add(answer);
                index++;
            }
        }

        if (older)
        {
            _answers.InsertRange(0, found);
            _olderCursor = page.NextCursor;
        }
        else
        {
            _answers.AddRange(found);
            if (_olderCursor is null) _olderCursor = page.NextCursor;
        }
        return (found.Count, found);
    }

    private void StopLocked()
    {
        _source?.Cancel();
        _source?.Dispose();
        _source = null;
        _threadId = null;
        _olderCursor = null;
        _position = -1;
        _known.Clear();
        _answers.Clear();
    }

    public void Dispose() => Stop();
}
