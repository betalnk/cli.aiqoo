namespace CodexVoice;

internal sealed record VoicePairingApprovalRequest(
    VoicePairingClaim Claim, string AccountEmail, string ClientName,
    DateTimeOffset ExpiresAt);

/// <summary>Owner interaction runs independently of the receive loop and its heartbeat.</summary>
internal sealed class CliPairingApprovalCoordinator : IAsyncDisposable
{
    private readonly VoicePairingManager _manager;
    private readonly Func<VoicePairingApprovalRequest, CancellationToken, Task<bool>> _approve;
    private readonly Func<VoicePairingClaim, bool, CancellationToken, Task> _send;
    private readonly CancellationToken _connection;
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _pending = new(StringComparer.Ordinal);

    internal CliPairingApprovalCoordinator(VoicePairingManager manager,
        Func<VoicePairingApprovalRequest, CancellationToken, Task<bool>> approve,
        Func<VoicePairingClaim, bool, CancellationToken, Task> send,
        CancellationToken connection)
    {
        _manager = manager;
        _approve = approve;
        _send = send;
        _connection = connection;
    }

    internal void Queue(VoicePairingClaim claim, string accountEmail, string clientName)
    {
        lock (_gate)
        {
            if (_connection.IsCancellationRequested || _pending.ContainsKey(claim.EventId)) return;
            foreach (var id in _pending.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray())
                _pending.Remove(id);
            if (_pending.Count >= 4) return; // Server will replay any unhandled pending request.
            _pending[claim.EventId] = Task.Run(() => HandleAsync(claim, accountEmail, clientName));
        }
    }

    private async Task HandleAsync(VoicePairingClaim claim, string email, string clientName)
    {
        var approved = false;
        try
        {
            var verified = _manager.VerifyClaim(claim);
            var remaining = verified.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("Pairing expired.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_connection);
            deadline.CancelAfter(remaining);
            if (verified.AlreadyApproved) approved = true;
            else
            {
                await _promptGate.WaitAsync(deadline.Token);
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    approved = await _approve(new(claim, email, clientName, verified.ExpiresAt), deadline.Token);
                }
                finally { _promptGate.Release(); }
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (approved) _manager.AcceptApproved(claim); // Recheck expiry/proof after the human decision.
            else _manager.Cancel(claim.PairId);
        }
        catch (OperationCanceledException) when (_connection.IsCancellationRequested) { return; }
        catch
        {
            approved = false;
            try { _manager.Cancel(claim.PairId); }
            catch { /* No grant was made. */ }
        }
        try { await _send(claim, approved, _connection); }
        catch { /* Approved decisions survive a lost ACK; unresolved requests are replayed by the server. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate) tasks = _pending.Values.ToArray();
        await Task.WhenAll(tasks);
        _promptGate.Dispose();
    }
}
