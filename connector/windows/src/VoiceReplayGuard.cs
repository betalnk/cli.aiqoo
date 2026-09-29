using System.Security.Cryptography;

namespace CodexVoice;

/// <summary>
/// Rejects duplicate authenticated envelope IDs and nonces during one process lifetime.
/// Mutating commands also need a durable request-outcome journal before delivery to Codex.
/// </summary>
internal sealed class VoiceReplayGuard
{
    private const int MaxEntries = 8192;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Queue<Entry> _oldest = new();
    private readonly HashSet<string> _requestIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nonces = new(StringComparer.Ordinal);

    internal VoiceReplayGuard(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    internal bool TryAccept(string clientId, string direction, string requestId, ReadOnlySpan<byte> nonce)
    {
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        VoicePairingCrypto.ValidateRequestId(requestId);
        if (direction is not ("browser" or "device"))
            throw new ArgumentException("Invalid encrypted direction.", nameof(direction));
        if (nonce.Length != 12) throw new ArgumentException("Expected a 12-byte nonce.", nameof(nonce));
        var scope = clientId + "\0" + direction + "\0";
        var requestTag = scope + requestId;
        var nonceTag = scope + VoicePairingCrypto.EncodeBase64Url(nonce);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            while (_oldest.TryPeek(out var old) && old.ExpiresAt <= now)
            {
                _oldest.Dequeue();
                _requestIds.Remove(old.RequestTag);
                _nonces.Remove(old.NonceTag);
            }
            if (_requestIds.Contains(requestTag) || _nonces.Contains(nonceTag)) return false;
            if (_oldest.Count >= MaxEntries)
                throw new CryptographicException("Replay window is full; reconnect or restart pairing.");
            _requestIds.Add(requestTag);
            _nonces.Add(nonceTag);
            _oldest.Enqueue(new Entry(requestTag, nonceTag, now.Add(Retention)));
            return true;
        }
    }

    private sealed record Entry(string RequestTag, string NonceTag, DateTimeOffset ExpiresAt);
}
