using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

internal enum VoiceSendState { New, Accepted, Queued, Unknown, Rejected, Conflict }

internal sealed record VoiceSendReceipt(
    VoiceSendState State, string? ErrorCode = null, string? ErrorMessage = null,
    string? TurnId = null);

/// <summary>
/// Durable at-most-once guard for a remote Codex send request. A crash between
/// submission and its reply leaves an unknown receipt; retrying that ID never sends again.
/// </summary>
internal sealed class VoiceSendJournal
{
    private const int MaxReceipts = 10_000;
    private readonly string _root;
    private readonly object _gate = new();

    internal VoiceSendJournal(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIQOO", "Voice", "send-journal-v1");
    }

    internal static string Digest(string threadId, string text)
    {
        if (!Guid.TryParseExact(threadId, "D", out var parsed) || parsed.ToString("D") != threadId)
            throw new ArgumentException("Expected a canonical Codex thread UUID.", nameof(threadId));
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes("message.send\0" + threadId + "\0" + text);
        try { return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal VoiceSendReceipt Reserve(string clientId, string requestId, string digest)
    {
        Validate(clientId, requestId, digest);
        lock (_gate)
        {
            var path = ReceiptPath(clientId, requestId);
            if (File.Exists(path)) return Read(clientId, requestId, digest);
            Directory.CreateDirectory(_root);
            if (Directory.EnumerateFiles(_root, "*.dpapi").Take(MaxReceipts).Count() >= MaxReceipts)
                throw new IOException("Remote send journal is full; revoke and re-pair a phone before sending more.");
            var entry = new Entry(clientId, requestId, digest, "reserved", null, null, null);
            if (!WriteNew(path, clientId, requestId, entry)) return Read(clientId, requestId, digest);
            return new VoiceSendReceipt(VoiceSendState.New);
        }
    }

    internal VoiceSendReceipt Read(string clientId, string requestId, string digest)
    {
        Validate(clientId, requestId, digest);
        lock (_gate)
        {
            var path = ReceiptPath(clientId, requestId);
            if (!File.Exists(path)) return new VoiceSendReceipt(VoiceSendState.New);
            var entry = ReadEntry(path, clientId, requestId);
            if (entry.Digest != digest) return new VoiceSendReceipt(VoiceSendState.Conflict);
            return entry.Status switch
            {
                "accepted" => new VoiceSendReceipt(VoiceSendState.Accepted, TurnId: entry.TurnId),
                "queued" => new VoiceSendReceipt(VoiceSendState.Queued), // Receipts from older builds.
                "rejected" => new VoiceSendReceipt(VoiceSendState.Rejected, entry.ErrorCode, entry.ErrorMessage),
                "reserved" or "unknown" => new VoiceSendReceipt(VoiceSendState.Unknown),
                _ => throw new CryptographicException("Invalid remote send receipt status.")
            };
        }
    }

    internal void Complete(string clientId, string requestId, string digest, VoiceSendState state,
        string? errorCode = null, string? errorMessage = null, string? turnId = null)
    {
        Validate(clientId, requestId, digest);
        if (state is not (VoiceSendState.Accepted or VoiceSendState.Unknown or VoiceSendState.Rejected) ||
            (state == VoiceSendState.Accepted && turnId is { Length: > 128 }) ||
            (state == VoiceSendState.Rejected && (string.IsNullOrWhiteSpace(errorCode) ||
                errorCode.Length > 64 || errorMessage is { Length: > 256 })))
            throw new ArgumentException("Invalid remote send error receipt.");
        lock (_gate)
        {
            var path = ReceiptPath(clientId, requestId);
            var old = ReadEntry(path, clientId, requestId);
            if (old.Digest != digest) throw new CryptographicException("Request ID was reused with another message.");
            if (old.Status != "reserved")
                throw new InvalidOperationException("Remote send request is already finished.");
            var entry = old with
            {
                Status = state switch
                {
                    VoiceSendState.Accepted => "accepted",
                    VoiceSendState.Unknown => "unknown",
                    _ => "rejected"
                },
                ErrorCode = state == VoiceSendState.Rejected ? errorCode : null,
                ErrorMessage = state == VoiceSendState.Rejected ? errorMessage : null,
                TurnId = state == VoiceSendState.Accepted ? turnId : null
            };
            WriteReplace(path, clientId, requestId, entry);
        }
    }

    private static void Validate(string clientId, string requestId, string digest)
    {
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        // The legacy RPC used UUIDs; CLI command v1 uses random 32-hex IDs.
        if (requestId.Length == 32) VoicePairingCrypto.ValidateHexId(requestId, nameof(requestId));
        else VoicePairingCrypto.ValidateRequestId(requestId);
        if (digest is null || digest.Length != 64 ||
            digest.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new ArgumentException("Expected a SHA-256 hex digest.", nameof(digest));
    }

    private string ReceiptPath(string clientId, string requestId)
    {
        var name = SHA256.HashData(Encoding.ASCII.GetBytes(clientId + "\0" + requestId));
        return Path.Combine(_root, Convert.ToHexString(name).ToLowerInvariant() + ".dpapi");
    }

    private static Entry ReadEntry(string path, string clientId, string requestId)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is < 1 or > 4096)
            throw new CryptographicException("Remote send receipt is missing or invalid.");
        var protectedBytes = File.ReadAllBytes(path);
        byte[]? clear = null;
        try
        {
            clear = WindowsUserDpapi.Unprotect(protectedBytes, Purpose(clientId, requestId));
            var entry = JsonSerializer.Deserialize<Entry>(clear)
                ?? throw new CryptographicException("Remote send receipt is empty.");
            if (entry.ClientId != clientId || entry.RequestId != requestId)
                throw new CryptographicException("Remote send receipt identity mismatch.");
            return entry;
        }
        catch (JsonException exception)
        {
            throw new CryptographicException("Remote send receipt is malformed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static bool WriteNew(string path, string clientId, string requestId, Entry entry)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(entry);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = WindowsUserDpapi.Protect(clear, Purpose(clientId, requestId));
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(protectedBytes);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException) when (File.Exists(path)) { return false; }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static void WriteReplace(string path, string clientId, string requestId, Entry entry)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(entry);
        byte[]? protectedBytes = null;
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            protectedBytes = WindowsUserDpapi.Protect(clear, Purpose(clientId, requestId));
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static string Purpose(string clientId, string requestId) =>
        "send-journal\0" + clientId + "\0" + requestId;

    private sealed record Entry(string ClientId, string RequestId, string Digest,
        string Status, string? ErrorCode, string? ErrorMessage, string? TurnId = null);
}
