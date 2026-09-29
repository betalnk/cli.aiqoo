using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexVoice;

internal sealed record CliCommandStart(string RequestId, string ClientId, string Op,
    string? ThreadId, int TotalBytes, int TotalChunks, string Nonce, string Ciphertext);

/// <summary>Validates the frozen command wire before any plaintext or local side effect.</summary>
internal static class CliCommandProtocol
{
    internal const int ChunkBytes = 8192;
    internal const int MaxAudioBytes = 4_320_000;

    internal static bool TryReadStart(JsonElement root, string deviceId,
        out CliCommandStart? start)
    {
        start = null;
        try
        {
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 10
                || root.GetProperty("type").GetString() != "command_start"
                || root.GetProperty("deviceId").GetString() != deviceId)
                return false;
            var requestId = root.GetProperty("requestId").GetString()!;
            var clientId = root.GetProperty("clientId").GetString()!;
            var op = root.GetProperty("op").GetString()!;
            var threadNode = root.GetProperty("threadId");
            var threadId = threadNode.ValueKind == JsonValueKind.Null ? null : threadNode.GetString();
            var totalBytes = root.GetProperty("totalBytes").GetInt32();
            var totalChunks = root.GetProperty("totalChunks").GetInt32();
            var nonce = root.GetProperty("nonce").GetString()!;
            var ciphertext = root.GetProperty("ciphertext").GetString()!;
            VoicePairingCrypto.ValidateHexId(requestId, nameof(requestId));
            VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
            if (op is not ("send_text" or "transcribe_audio")
                || (op == "send_text" && (threadId is null
                    || !SessionResolver.TryCanonicalThreadId(threadId, out _)))
                || (op == "transcribe_audio" && threadId is not null)
                || totalBytes < (op == "send_text" ? 1 : 2)
                || totalBytes > (op == "send_text" ? ChunkBytes : MaxAudioBytes)
                || (op == "transcribe_audio" && (totalBytes & 1) != 0)
                || totalChunks != (totalBytes + ChunkBytes - 1) / ChunkBytes
                || nonce is null || ciphertext is null
                || nonce.Length > 32 || ciphertext.Length > 2048)
                return false;
            start = new CliCommandStart(requestId, clientId, op, threadId,
                totalBytes, totalChunks, nonce, ciphertext);
            return true;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException
            or FormatException or ArgumentException or OverflowException or JsonException)
        { return false; }
    }

    internal static bool ValidateMetadata(CliCommandStart start, ReadOnlySpan<byte> plaintext,
        out int sampleRate)
    {
        sampleRate = 0;
        try
        {
            using var document = JsonDocument.Parse(plaintext.ToArray(), new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != (start.Op == "send_text" ? 5 : 8)
                || root.GetProperty("v").GetInt32() != 1
                || root.GetProperty("op").GetString() != start.Op
                || root.GetProperty("totalBytes").GetInt32() != start.TotalBytes
                || root.GetProperty("totalChunks").GetInt32() != start.TotalChunks)
                return false;
            var thread = root.GetProperty("threadId");
            if (start.ThreadId is null && thread.ValueKind != JsonValueKind.Null
                || start.ThreadId is not null && thread.GetString() != start.ThreadId)
                return false;
            if (start.Op == "transcribe_audio")
            {
                sampleRate = root.GetProperty("sampleRate").GetInt32();
                if (root.GetProperty("format").GetString() != "pcm_s16le"
                    || root.GetProperty("channels").GetInt32() != 1
                    || sampleRate is < 8000 or > 48000
                    || start.TotalBytes > (long)sampleRate * 2 * 45)
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException
            or FormatException or ArgumentException or OverflowException or JsonException)
        { return false; }
    }

    internal static int ExpectedChunkLength(CliCommandStart start, int seq)
    {
        if (seq < 1 || seq > start.TotalChunks) return -1;
        return seq < start.TotalChunks ? ChunkBytes
            : start.TotalBytes - ChunkBytes * (start.TotalChunks - 1);
    }

    internal static string CropTranscript(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= ChunkBytes) return text;
        const string ellipsis = "…";
        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength + 3 > ChunkBytes) break;
            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }
        return text[..chars] + ellipsis;
    }
}

/// <summary>Ephemeral encrypted commands for one connected desktop WebSocket.</summary>
internal sealed class CliCommandManager : IAsyncDisposable
{
    private readonly VoicePairingStore _store;
    private readonly string _deviceId;
    private readonly Func<object, CancellationToken, Task> _send;
    private readonly Func<bool> _localInputIdle;
    private readonly Func<bool> _desktopReady;
    private readonly CancellationToken _connectionToken;
    private readonly VoiceRemoteAudio _audio = new();
    private readonly VoiceSendJournal _journal = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexVoice", "cli-send-journal-v1"));
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private Command? _active;
    private bool _disposed;

    private sealed class Command(CliCommandStart start, int sampleRate, byte[] key,
        CancellationToken connectionToken)
    {
        internal CliCommandStart Start { get; } = start;
        internal int SampleRate { get; } = sampleRate;
        internal byte[] Key { get; } = key;
        internal CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        internal Channel<byte[]> AudioChunks { get; } = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        internal MemoryStream Text { get; } = new();
        internal string AudioStreamId { get; } = Guid.NewGuid().ToString("D");
        internal int NextSeq = 1;
        internal int ReceivedBytes;
        internal bool Ended;
        internal bool Terminal;
        internal Task Worker = Task.CompletedTask;
        internal int Cleaned;
    }

    internal CliCommandManager(VoicePairingStore store, string deviceId,
        Func<object, CancellationToken, Task> send, Func<bool> localInputIdle,
        CancellationToken connectionToken, Func<bool>? desktopReady = null)
    {
        _store = store;
        VoicePairingCrypto.ValidateHexId(deviceId, nameof(deviceId));
        _deviceId = deviceId;
        _send = send;
        _localInputIdle = localInputIdle;
        _desktopReady = desktopReady ?? CliInteractiveDesktopGuard.IsReady;
        _connectionToken = connectionToken;
    }

    internal async Task HandleStartAsync(JsonElement root)
    {
        if (!CliCommandProtocol.TryReadStart(root, _deviceId, out var start) || start is null)
            return;
        byte[]? key = null;
        try
        {
            key = _store.LoadClientKey(start.ClientId);
            if (key is null) return;
            var clear = CliCommandCrypto.Decrypt(key, _deviceId, start.ClientId,
                start.RequestId, start.Op, start.ThreadId, "browser", "start", 0, "",
                start.Nonce, start.Ciphertext, 1024);
            int sampleRate;
            try
            {
                if (!CliCommandProtocol.ValidateMetadata(start, clear, out sampleRate))
                {
                    await SendAckAsync(start, key, 1, "rejected", "invalid_payload");
                    return;
                }
            }
            finally { CryptographicOperations.ZeroMemory(clear); }

            if (!_desktopReady())
            {
                await SendAckAsync(start, key, 1, "rejected", "locked");
                return;
            }

            Command? command = null;
            lock (_gate)
            {
                if (!_disposed)
                {
                    PruneSeen();
                    if (_active is null && !_seen.ContainsKey(start.RequestId))
                    {
                        command = new Command(start, sampleRate, key, _connectionToken);
                        _active = command;
                        _seen[start.RequestId] = DateTimeOffset.UtcNow;
                        key = null;
                    }
                }
            }
            if (command is null)
            {
                await SendAckAsync(start, key!, 1, "rejected", "unavailable");
                return;
            }
            try
            {
                await SendAckAsync(start, command.Key, 1, "ready");
                if (start.Op == "transcribe_audio")
                    StartWorker(command, () => ProcessAudioAsync(command));
                _ = WatchDeadlineAsync(command);
            }
            catch
            {
                await FinishAsync(command, null, 0, null);
                throw;
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException
            or ArgumentException or JsonException)
        {
            if (key is not null)
                try { await SendAckAsync(start, key, 1, "rejected", "invalid_payload"); }
                catch { }
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    internal async Task HandleChunkAsync(JsonElement root)
    {
        Command? command = null;
        byte[]? clear = null;
        try
        {
            var requestId = root.GetProperty("requestId").GetString()!;
            var clientId = root.GetProperty("clientId").GetString()!;
            var seq = root.GetProperty("seq").GetInt32();
            VoicePairingCrypto.ValidateHexId(requestId, nameof(requestId));
            VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
            lock (_gate)
                if (_active is { Terminal: false } active
                    && active.Start.RequestId == requestId && active.Start.ClientId == clientId)
                    command = active;
            if (command is null) return;
            var start = command.Start;
            if (start.Op == "transcribe_audio" && !_desktopReady())
            {
                await FinishAsync(command, "rejected", 2, "locked");
                return;
            }
            if (root.EnumerateObject().Count() != 6 || command.Ended || seq != command.NextSeq
                || seq > start.TotalChunks)
                throw new InvalidDataException("Invalid command chunk sequence.");
            clear = CliCommandCrypto.Decrypt(command.Key, _deviceId, start.ClientId,
                start.RequestId, start.Op, start.ThreadId, "browser", "chunk", seq, "",
                root.GetProperty("nonce").GetString()!,
                root.GetProperty("ciphertext").GetString()!, CliCommandProtocol.ChunkBytes);
            if (clear.Length != CliCommandProtocol.ExpectedChunkLength(start, seq)
                || start.Op == "transcribe_audio" && (clear.Length & 1) != 0)
                throw new InvalidDataException("Invalid command chunk length.");
            command.NextSeq++;
            command.ReceivedBytes += clear.Length;
            if (start.Op == "send_text") command.Text.Write(clear);
            else
            {
                if (!command.AudioChunks.Writer.TryWrite(clear))
                    throw new InvalidDataException("Phone audio stream has ended.");
                clear = null; // The audio worker owns and zeros this buffer.
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException
            or ArgumentException or InvalidOperationException or InvalidDataException or JsonException)
        {
            if (command is not null)
                await FinishAsync(command, "rejected", 2, "invalid_payload");
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    internal async Task HandleEndAsync(JsonElement root)
    {
        if (!TryReadControl(root, "command_end", out var command)) return;
        if (command.Ended || command.NextSeq != command.Start.TotalChunks + 1
            || command.ReceivedBytes != command.Start.TotalBytes)
        {
            await FinishAsync(command, "rejected", 2, "invalid_payload");
            return;
        }
        command.Ended = true;
        if (command.Start.Op == "transcribe_audio") command.AudioChunks.Writer.TryComplete();
        else StartWorker(command, () => ProcessSendAsync(command));
    }

    internal void HandleCancel(JsonElement root)
    {
        if (!TryReadControl(root, "command_cancel", out var command)) return;
        _ = FinishAsync(command, null, 0, null);
    }

    private bool TryReadControl(JsonElement root, string type, out Command command)
    {
        command = null!;
        try
        {
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || root.GetProperty("type").GetString() != type)
                return false;
            var requestId = root.GetProperty("requestId").GetString();
            var clientId = root.GetProperty("clientId").GetString();
            lock (_gate)
            {
                if (_active is not { Terminal: false } active
                    || active.Start.RequestId != requestId || active.Start.ClientId != clientId)
                    return false;
                command = active;
                return true;
            }
        }
        catch { return false; }
    }

    private async Task ProcessAudioAsync(Command command)
    {
        var clientId = command.Start.ClientId;
        var streamId = command.AudioStreamId;
        var nextAudioSeq = 0;
        var started = false;
        try
        {
            if (!_desktopReady())
                throw new VoiceAudioException("locked", "PC is locked.");
            await _audio.StartAsync(clientId, streamId, "pcm_s16le", command.SampleRate,
                1, command.Cancellation.Token, maxSeconds: 45,
                maxChunkBytes: CliCommandProtocol.ChunkBytes);
            started = true;
            await foreach (var chunk in command.AudioChunks.Reader.ReadAllAsync(command.Cancellation.Token))
            {
                try
                {
                    if (!_desktopReady())
                        throw new VoiceAudioException("locked", "PC was locked during upload.");
                    await _audio.ChunkBytesAsync(clientId, streamId, nextAudioSeq,
                        chunk, command.Cancellation.Token);
                    nextAudioSeq++;
                }
                finally { CryptographicOperations.ZeroMemory(chunk); }
            }
            if (!command.Ended || nextAudioSeq != command.Start.TotalChunks)
                throw new InvalidDataException("Incomplete phone audio.");
            if (!_desktopReady())
                throw new VoiceAudioException("locked", "PC was locked during recognition.");
            var text = await _audio.EndTextAsync(clientId, streamId, nextAudioSeq,
                command.Cancellation.Token);
            started = false;
            if (!_desktopReady())
                throw new VoiceAudioException("locked", "PC was locked during recognition.");
            text = CliCommandProtocol.CropTranscript(text);
            await FinishAsync(command, "transcribed", 2, null, text);
        }
        catch (OperationCanceledException) when (command.Cancellation.IsCancellationRequested) { }
        catch (VoiceAudioException exception) when (exception.Code == "locked")
        {
            await FinishAsync(command, "rejected", 2, "locked");
        }
        catch (Exception)
        {
            await FinishAsync(command, "rejected", 2, "asr_failed");
        }
        finally
        {
            if (started)
                try { await _audio.CancelAsync(clientId, streamId); }
                catch { }
            while (command.AudioChunks.Reader.TryRead(out var pending))
                CryptographicOperations.ZeroMemory(pending);
        }
    }

    private static void StartWorker(Command command, Func<Task> work)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        command.Worker = Task.Run(async () =>
        {
            await ready.Task;
            await work();
        });
        ready.SetResult();
    }

    private async Task WatchDeadlineAsync(Command command)
    {
        var deadline = command.Start.Op == "transcribe_audio" ? 300 : 180;
        try { await Task.Delay(TimeSpan.FromSeconds(deadline), command.Cancellation.Token); }
        catch (OperationCanceledException) { return; }
        await FinishAsync(command, null, 0, null);
    }

    private async Task ProcessSendAsync(Command command)
    {
        var start = command.Start;
        var text = "";
        string? digest = null;
        var reserved = false;
        var enterIssued = false;
        var submittedAckSent = false;
        try
        {
            var textBytes = command.Text.ToArray();
            try { text = new UTF8Encoding(false, true).GetString(textBytes); }
            finally { CryptographicOperations.ZeroMemory(textBytes); }
            if (Encoding.UTF8.GetByteCount(text) != start.TotalBytes
                || ForegroundTerminalSender.PrepareText(text) is null)
            {
                await FinishAsync(command, "rejected", 2, "invalid_payload");
                return;
            }
            if (!_desktopReady())
            {
                await FinishAsync(command, "rejected", 2, "locked");
                return;
            }
            if (!_localInputIdle())
            {
                await FinishAsync(command, "rejected", 2, "unavailable");
                return;
            }
            var selected = SessionResolver.ListOpenSessions()
                .SingleOrDefault(item => item.ThreadId == start.ThreadId);
            if (selected is null || !SessionResolver.TryValidate(selected, out _))
            {
                await FinishAsync(command, "rejected", 2, "not_visible");
                return;
            }
            if (!CodexRolloutReceiptVerifier.TryCapture(start.ThreadId!, out var baseline, out _)
                || baseline is null)
            {
                await FinishAsync(command, "rejected", 2, "unavailable");
                return;
            }
            digest = VoiceSendJournal.Digest(start.ThreadId!, text);
            var reservation = _journal.Reserve(start.ClientId, start.RequestId, digest);
            if (reservation.State != VoiceSendState.New)
            {
                // Any durable receipt means this ID was already attempted. Even a
                // confirmed earlier attempt must never be replayed under a fresh route.
                await FinishAsync(command, "unknown", 2, "device_error");
                return;
            }
            reserved = true;
            var result = await ForegroundTerminalSender.SendAsync(selected, text,
                command.Cancellation.Token,
                () => _desktopReady() && _localInputIdle());
            if (!result.Entered)
            {
                _journal.Complete(start.ClientId, start.RequestId, digest, VoiceSendState.Unknown);
                reserved = false;
                await FinishAsync(command, "unknown", 2, "input_failed");
                return;
            }
            enterIssued = true;
            try
            {
                await SendAckAsync(start, command.Key, 2, "submitted");
                submittedAckSent = true;
            }
            catch { /* A lost browser route does not cancel receipt verification. */ }
            var confirmed = await CodexRolloutReceiptVerifier.WaitForUserMessageAsync(
                baseline, result.SubmittedText, command.Cancellation.Token);
            _journal.Complete(start.ClientId, start.RequestId, digest,
                confirmed ? VoiceSendState.Accepted : VoiceSendState.Unknown);
            reserved = false;
            await FinishAsync(command,
                submittedAckSent ? (confirmed ? "confirmed" : "unknown") : null,
                submittedAckSent ? 3 : 0,
                confirmed ? null : "receipt_timeout");
        }
        catch (OperationCanceledException) when (command.Cancellation.IsCancellationRequested)
        {
            if (reserved && digest is not null)
                TryMarkUnknown(start, digest);
            await FinishAsync(command, null, 0, null);
        }
        catch
        {
            if (reserved && digest is not null)
            {
                TryMarkUnknown(start, digest);
                await FinishAsync(command, enterIssued && !submittedAckSent ? null : "unknown",
                    submittedAckSent ? 3 : 2, "device_error");
            }
            else await FinishAsync(command, enterIssued && !submittedAckSent ? null
                : enterIssued ? "unknown" : "rejected", submittedAckSent ? 3 : 2,
                "device_error");
        }
        finally
        {
            var bytes = command.Text.GetBuffer();
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void TryMarkUnknown(CliCommandStart start, string digest)
    {
        try { _journal.Complete(start.ClientId, start.RequestId, digest, VoiceSendState.Unknown); }
        catch { }
    }

    private async Task FinishAsync(Command command, string? stage, int ackSeq,
        string? reason, string? text = null)
    {
        lock (_gate)
        {
            if (command.Terminal) return;
            command.Terminal = true;
            if (ReferenceEquals(_active, command)) _active = null;
        }
        command.AudioChunks.Writer.TryComplete();
        command.Cancellation.Cancel();
        try
        {
            if (stage is not null)
                await SendAckAsync(command.Start, command.Key, ackSeq, stage, reason, text);
        }
        catch { /* Server may already have removed the ephemeral route. */ }
        finally
        {
            _ = Task.Run(async () =>
            {
                try { await command.Worker; }
                catch { }
                Cleanup(command);
            });
        }
    }

    private async Task SendAckAsync(CliCommandStart start, byte[] key, int seq,
        string stage, string? reason = null, string? text = null)
    {
        object plain = stage switch
        {
            "rejected" => new { v = 1, stage, reason, maybeEntered = false },
            "unknown" => new { v = 1, stage, reason, maybeEntered = true },
            "transcribed" => new { v = 1, stage, text },
            _ => new { v = 1, stage }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plain);
        try
        {
            var encrypted = CliCommandCrypto.Encrypt(key, _deviceId, start.ClientId,
                start.RequestId, start.Op, start.ThreadId, "device", "ack", seq,
                stage, bytes);
            await _send(new { type = "command_ack", requestId = start.RequestId,
                clientId = start.ClientId, ackSeq = seq, stage,
                nonce = encrypted.Nonce, ciphertext = encrypted.Ciphertext },
                _connectionToken);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Cleanup(Command command)
    {
        if (Interlocked.Exchange(ref command.Cleaned, 1) != 0) return;
        while (command.AudioChunks.Reader.TryRead(out var chunk))
            CryptographicOperations.ZeroMemory(chunk);
        CryptographicOperations.ZeroMemory(command.Key);
        try { CryptographicOperations.ZeroMemory(command.Text.GetBuffer()); }
        catch (ObjectDisposedException) { }
        command.Text.Dispose();
        command.Cancellation.Dispose();
    }

    private void PruneSeen()
    {
        var before = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var id in _seen.Where(pair => pair.Value < before).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
    }

    public async ValueTask DisposeAsync()
    {
        Command? command;
        lock (_gate)
        {
            _disposed = true;
            command = _active;
            _active = null;
            if (command is not null) command.Terminal = true;
        }
        if (command is not null)
        {
            command.Cancellation.Cancel();
            command.AudioChunks.Writer.TryComplete();
            try { await command.Worker; }
            catch { }
            Cleanup(command);
        }
        await _audio.DisposeAsync();
    }
}
