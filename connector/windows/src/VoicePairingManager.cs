using System.Security.Cryptography;

namespace CodexVoice;

internal sealed record VoicePairingOffer(
    string PairId, string DisplayCode, string DevicePublicKey, string Fingerprint,
    string QrUrl, DateTimeOffset ExpiresAt);

internal sealed record VoicePairingClaim(
    string EventId, string PairId, string ClientId, string ClientPublicKey, string Proof);

internal sealed record VoicePairingAccepted(
    string EventId, string PairId, string ClientId, string ClientPublicKey,
    string Proof, DateTimeOffset ReplayUntil);

/// <summary>
/// Keeps QR pair secrets in memory until expiry. Relay pair IDs and codes enter through Begin;
/// only a locally verified claim can create a protected client key.
/// </summary>
internal sealed class VoicePairingManager : IDisposable
{
    private const int MaxPendingPairs = 4;
    private readonly VoicePairingStore _store;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPair> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VoicePairingAccepted> _recentAccepts = new(StringComparer.Ordinal);
    private bool _disposed;

    internal VoicePairingManager(VoicePairingStore store, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    internal VoicePairingOffer Begin(string pairId, string displayedCode, long relayExpiresAtUnixSeconds)
    {
        VoicePairingCrypto.ValidateHexId(pairId, nameof(pairId));
        var code = VoicePairingCrypto.NormalizeCode(displayedCode);
        var now = _clock.GetUtcNow();
        var relayExpiry = DateTimeOffset.FromUnixTimeSeconds(relayExpiresAtUnixSeconds);
        if (relayExpiry <= now) throw new ArgumentException("The relay pairing has expired.", nameof(relayExpiresAtUnixSeconds));
        var expiresAt = relayExpiry < now.AddSeconds(VoicePairingCrypto.PairingSeconds)
            ? relayExpiry : now.AddSeconds(VoicePairingCrypto.PairingSeconds);
        using var deviceKey = _store.LoadOrCreateDeviceKey();
        var spki = VoicePairingCrypto.ExportPublicKey(deviceKey);
        var secret = VoicePairingCrypto.CreatePairSecret();
        try
        {
            var qr = VoicePairingCrypto.BuildQrUrl(pairId, code, secret, spki);
            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpired(now);
                if (_pending.Count >= MaxPendingPairs)
                    throw new InvalidOperationException("Too many pending phone pairings.");
                if (_pending.ContainsKey(pairId))
                    throw new InvalidOperationException("This pairing is already pending.");
                _store.SavePendingPair(pairId, secret, expiresAt);
                _pending.Add(pairId, new PendingPair(secret, expiresAt));
                secret = Array.Empty<byte>(); // PendingPair now owns the random secret.
            }
            return new VoicePairingOffer(pairId, displayedCode,
                VoicePairingCrypto.EncodeBase64Url(spki), VoicePairingCrypto.Fingerprint(spki), qr, expiresAt);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal VoicePairingAccepted Accept(VoicePairingClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        VoicePairingCrypto.ValidateHexId(claim.EventId, nameof(claim.EventId));
        VoicePairingCrypto.ValidateHexId(claim.PairId, nameof(claim.PairId));
        VoicePairingCrypto.ValidateHexId(claim.ClientId, nameof(claim.ClientId));
        using var clientKey = VoicePairingCrypto.ImportPublicKey(claim.ClientPublicKey);
        var clientSpki = clientKey.ExportSubjectPublicKeyInfo();
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            ThrowIfDisposed();
            PruneExpired(now);
            var durable = _store.LoadDecision(claim.EventId, now);
            if (durable is not null)
            {
                if (durable.PairId != claim.PairId || durable.ClientId != claim.ClientId
                    || durable.ClientPublicKey != claim.ClientPublicKey || durable.Proof != claim.Proof)
                    throw new CryptographicException("Pairing event ID was reused with different claim data.");
                return new VoicePairingAccepted(claim.EventId, claim.PairId, claim.ClientId,
                    claim.ClientPublicKey, claim.Proof,
                    DateTimeOffset.FromUnixTimeSeconds(durable.ExpiresAtUnixSeconds));
            }
            if (_recentAccepts.TryGetValue(claim.EventId, out var accepted))
            {
                if (accepted.PairId != claim.PairId || accepted.ClientId != claim.ClientId ||
                    accepted.ClientPublicKey != claim.ClientPublicKey || accepted.Proof != claim.Proof)
                    throw new CryptographicException("Pairing event ID was reused with different claim data.");
                return accepted;
            }
            var fromMemory = _pending.TryGetValue(claim.PairId, out var pending);
            if (!fromMemory)
            {
                var saved = _store.LoadPendingPair(claim.PairId, now)
                    ?? throw new CryptographicException("No unexpired local secret for this pairing.");
                pending = new PendingPair(saved.Secret,
                    DateTimeOffset.FromUnixTimeSeconds(saved.ExpiresAtUnixSeconds));
            }
            try
            {
                if (!VoicePairingCrypto.VerifyProof(pending!.Secret, claim.PairId, clientSpki, claim.Proof))
                    throw new CryptographicException("Phone pairing proof did not match the QR secret.");

                using var deviceKey = _store.LoadOrCreateDeviceKey();
                var aesKey = VoicePairingCrypto.DeriveClientKey(deviceKey, claim.ClientPublicKey,
                    pending.Secret, claim.PairId, claim.ClientId);
                try { _store.SaveClientKey(claim.ClientId, aesKey); }
                finally { CryptographicOperations.ZeroMemory(aesKey); }

                _store.SaveDecision(new VoicePendingPairDecision(claim.EventId, claim.PairId,
                    claim.ClientId, claim.ClientPublicKey, claim.Proof,
                    pending.ExpiresAt.ToUnixTimeSeconds()));
                if (fromMemory) _pending.Remove(claim.PairId);
                accepted = new VoicePairingAccepted(claim.EventId, claim.PairId, claim.ClientId,
                    claim.ClientPublicKey, claim.Proof, pending.ExpiresAt);
                _recentAccepts.Add(claim.EventId, accepted);
                return accepted;
            }
            finally
            {
                if (!fromMemory || _recentAccepts.ContainsKey(claim.EventId)) pending!.Dispose();
            }
        }
    }

    internal IReadOnlyList<VoicePendingPairDecision> PendingDecisions() =>
        _store.ListDecisions(_clock.GetUtcNow());

    internal void ConfirmDecision(string eventId, string pairId, string clientId)
    {
        lock (_gate)
        {
            var decision = _store.LoadDecision(eventId, _clock.GetUtcNow());
            if (decision is null || decision.PairId != pairId || decision.ClientId != clientId) return;
            _store.DeleteDecision(eventId);
            _store.DeletePendingPair(pairId);
            _recentAccepts.Remove(eventId);
        }
    }

    internal void RejectDecision(string eventId, string pairId, string clientId)
    {
        lock (_gate)
        {
            var decision = _store.LoadDecision(eventId, _clock.GetUtcNow());
            if (decision is null || decision.PairId != pairId || decision.ClientId != clientId) return;
            _store.RemoveClientKey(clientId);
            _store.DeleteDecision(eventId);
            _store.DeletePendingPair(pairId);
            _recentAccepts.Remove(eventId);
        }
    }

    internal void RejectAcceptedClient(string clientId)
    {
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        lock (_gate)
        {
            ThrowIfDisposed();
            _store.RemoveClientKey(clientId);
            foreach (var eventId in _recentAccepts.Where(p => p.Value.ClientId == clientId)
                         .Select(p => p.Key).ToArray())
                _recentAccepts.Remove(eventId);
        }
    }

    internal void Cancel(string pairId)
    {
        VoicePairingCrypto.ValidateHexId(pairId, nameof(pairId));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pending.Remove(pairId, out var pending)) pending.Dispose();
            _store.DeletePendingPair(pairId);
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var id in _pending.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray())
        {
            var pending = _pending[id];
            _pending.Remove(id);
            pending.Dispose();
            _store.DeletePendingPair(id);
        }
        // Event replay support lasts no longer than the pairing itself.
        foreach (var id in _recentAccepts.Where(p => p.Value.ReplayUntil <= now)
                     .Select(p => p.Key).ToArray())
            _recentAccepts.Remove(id);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VoicePairingManager));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _pending.Values) pending.Dispose();
            _pending.Clear();
            _recentAccepts.Clear();
        }
    }

    private sealed class PendingPair : IDisposable
    {
        internal PendingPair(byte[] secret, DateTimeOffset expiresAt)
        {
            Secret = secret;
            ExpiresAt = expiresAt;
        }

        internal byte[] Secret { get; }
        internal DateTimeOffset ExpiresAt { get; }
        public void Dispose() => CryptographicOperations.ZeroMemory(Secret);
    }
}
