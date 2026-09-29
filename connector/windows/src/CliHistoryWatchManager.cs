using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

/// <summary>Serves read-only snapshots of visible, exact Codex CLI sessions to paired browsers.</summary>
internal sealed class CliHistoryWatchManager : IAsyncDisposable
{
    private readonly VoicePairingStore _store;
    private readonly string _deviceId;
    private readonly ClientWebSocket _socket;
    private readonly CancellationToken _connectionToken;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    private sealed class Watch(string clientId, CancellationTokenSource cancellation)
    {
        internal string ClientId { get; } = clientId;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    internal CliHistoryWatchManager(VoicePairingStore store, string deviceId,
        ClientWebSocket socket, CancellationToken connectionToken)
    {
        _store = store;
        VoicePairingCrypto.ValidateHexId(deviceId, nameof(deviceId));
        _deviceId = deviceId;
        _socket = socket;
        _connectionToken = connectionToken;
    }

    internal void HandleRequest(JsonElement root)
    {
        if (!TryReadIds(root, out var requestId, out var clientId)
            || !root.TryGetProperty("kind", out var kindNode)
            || kindNode.ValueKind != JsonValueKind.String) return;
        var kind = kindNode.GetString();
        string? threadId = null;
        if (kind == "thread")
        {
            if (!root.TryGetProperty("threadId", out var threadNode)
                || threadNode.ValueKind != JsonValueKind.String
                || !SessionResolver.TryCanonicalThreadId(threadNode.GetString(), out threadId)) return;
        }
        else if (kind != "threads"
                 || root.TryGetProperty("threadId", out var listThread)
                 && listThread.ValueKind != JsonValueKind.Null) return;

        lock (_gate)
        {
            if (_disposed || _watches.ContainsKey(requestId) || _watches.Count >= 16) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_connectionToken);
            var watch = new Watch(clientId, cancellation);
            _watches.Add(requestId, watch);
            watch.Task = Task.Run(() => RunWatchAsync(watch, requestId, clientId,
                kind!, threadId, cancellation.Token), CancellationToken.None);
        }
    }

    internal void HandleCancel(JsonElement root)
    {
        if (!TryReadIds(root, out var requestId, out var clientId)) return;
        lock (_gate)
            if (_watches.TryGetValue(requestId, out var watch)
                && watch.ClientId == clientId)
            {
                _watches.Remove(requestId);
                watch.Cancellation.Cancel();
            }
    }

    internal async Task SendJsonAsync(object message, CancellationToken token)
    {
        var frame = JsonSerializer.SerializeToUtf8Bytes(message);
        if (frame.Length > 131072) throw new InvalidDataException("History frame is too large.");
        await _sendGate.WaitAsync(token);
        try { await _socket.SendAsync(frame, WebSocketMessageType.Text, true, token); }
        finally { _sendGate.Release(); }
    }

    private async Task RunWatchAsync(Watch watch, string requestId, string clientId,
        string kind, string? threadId, CancellationToken token)
    {
        byte[]? key = null;
        try
        {
            key = _store.LoadClientKey(clientId)
                ?? throw new InvalidOperationException("No paired browser key.");
            var previous = "";
            (string Path, long Length, long Ticks)? previousStamp = null;
            var sequence = 0;
            while (!token.IsCancellationRequested)
            {
                if (kind == "thread")
                {
                    var stamp = ReadThreadFileStamp(threadId!);
                    if (stamp == previousStamp)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                        continue;
                    }
                    previousStamp = stamp;
                }
                var snapshot = kind == "threads" ? ReadThreadsSnapshot()
                    : await ReadThreadSnapshotAsync(threadId!, token);
                // capturedAt is intentionally excluded from change detection.
                var stable = JsonSerializer.Serialize(snapshot);
                if (stable != previous)
                {
                    previous = stable;
                    object plain = kind == "threads"
                        ? new { kind, threads = snapshot, capturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                        : new { kind, threadId, messages = snapshot, capturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    var payload = JsonSerializer.SerializeToUtf8Bytes(plain);
                    if (sequence == int.MaxValue) throw new InvalidDataException("History sequence ended.");
                    sequence++;
                    var encrypted = CliHistoryCrypto.Encrypt(key, _deviceId, clientId,
                        requestId, kind, threadId, sequence, payload);
                    await SendJsonAsync(new { type = "history_packet", requestId, clientId,
                        seq = sequence, nonce = encrypted.Nonce,
                        ciphertext = encrypted.Ciphertext }, token);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
            {
                try { await SendJsonAsync(new { type = "history_unavailable", requestId, clientId }, token); }
                catch { /* The socket may have closed before the control packet. */ }
            }
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            lock (_gate)
                if (_watches.TryGetValue(requestId, out var current)
                    && ReferenceEquals(current, watch)) _watches.Remove(requestId);
            watch.Cancellation.Dispose();
        }
    }

    private static object[] ReadThreadsSnapshot()
    {
        return SessionResolver.ListOpenSessions()
            .Select(session =>
            {
                long? updatedAt = null;
                if (SessionResolver.TryGetRolloutPath(session.ThreadId, out var path))
                {
                    try { updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds(); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return new { threadId = session.ThreadId,
                    title = Crop(session.Name + " · " + session.Project, 160), updatedAt };
            })
            .OrderByDescending(session => session.updatedAt)
            .ThenBy(session => session.title, StringComparer.Ordinal)
            .Take(100)
            .Cast<object>().ToArray();
    }

    private static async Task<object[]> ReadThreadSnapshotAsync(string threadId,
        CancellationToken token)
    {
        var page = await CodexHistoryReader.ReadPageAsync(threadId, 12,
            cancellationToken: token);
        return page.Turns.Reverse().SelectMany(turn => turn.Messages.Select(message =>
            new { role = message.Role, text = CropUtf8(message.Text, 6500),
                createdAt = turn.StartedAtUnixSeconds,
                truncated = message.Truncated || Encoding.UTF8.GetByteCount(message.Text) > 6500 }))
            .TakeLast(3).Cast<object>().ToArray();
    }

    private static (string Path, long Length, long Ticks) ReadThreadFileStamp(string threadId)
    {
        var session = SessionResolver.ListOpenSessions()
            .SingleOrDefault(item => item.ThreadId == threadId);
        if (session is null || !SessionResolver.TryValidate(session, out _)
            || !SessionResolver.TryGetRolloutPath(threadId, out var path))
            throw new InvalidOperationException("Selected Codex session is no longer visible.");
        var info = new FileInfo(path);
        return (path, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static string Crop(string input, int maxChars) =>
        input.Length > maxChars ? input[..(maxChars - 1)] + "…" : input;

    private static string CropUtf8(string input, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(input) <= maxBytes) return input;
        var bytes = 0;
        var chars = 0;
        foreach (var rune in input.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size + 3 > maxBytes) break;
            bytes += size;
            chars += rune.Utf16SequenceLength;
        }
        return input[..chars] + "…";
    }

    private static bool TryReadIds(JsonElement root, out string requestId, out string clientId)
    {
        requestId = clientId = "";
        try
        {
            requestId = root.GetProperty("requestId").GetString()!;
            clientId = root.GetProperty("clientId").GetString()!;
            VoicePairingCrypto.ValidateHexId(requestId, nameof(requestId));
            VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
            return true;
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            _disposed = true;
            foreach (var watch in _watches.Values) watch.Cancellation.Cancel();
            tasks = _watches.Values.Select(watch => watch.Task).ToArray();
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        _sendGate.Dispose();
    }
}
