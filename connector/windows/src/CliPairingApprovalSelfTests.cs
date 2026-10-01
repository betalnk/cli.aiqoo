using System.Security.Cryptography;

namespace CodexVoice;

internal static class CliPairingApprovalSelfTests
{
    internal static async Task RunAsync()
    {
        foreach (var mode in new[] { "ok", "cancel", "disconnect", "expired", "bad-proof" })
            await ScenarioAsync(mode);
        await LegacyDecisionAsync();
    }

    private static async Task ScenarioAsync(string mode)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "codexvoice-approval-test-" + Guid.NewGuid().ToString("N")));
        try
        {
            var store = new VoicePairingStore(root);
            var clock = new Clock();
            using var manager = new VoicePairingManager(store, clock);
            var pairId = Guid.NewGuid().ToString("N");
            var clientId = Guid.NewGuid().ToString("N");
            var offer = manager.Begin(pairId, "2345-6789-ABCD", clock.Now.AddMinutes(5).ToUnixTimeSeconds());
            var encoded = new Uri(offer.QrUrl).Fragment.Split('&')
                .Single(x => x.StartsWith("secret=", StringComparison.Ordinal))[7..];
            var secret = VoicePairingCrypto.DecodeBase64Url(encoded, 32);
            using var browser = VoicePairingCrypto.CreateDeviceKey();
            var publicSpki = VoicePairingCrypto.ExportPublicKey(browser);
            var proof = VoicePairingCrypto.EncodeBase64Url(VoicePairingCrypto.CreateProof(secret, pairId, publicSpki));
            var claim = new VoicePairingClaim(Guid.NewGuid().ToString("N"), pairId, clientId,
                VoicePairingCrypto.EncodeBase64Url(publicSpki), mode == "bad-proof" ? new string('A', 43) : proof);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompts = 0;
            using var connection = new CancellationTokenSource();
            var approval = new CliPairingApprovalCoordinator(manager, async (_, token) =>
            {
                Interlocked.Increment(ref prompts);
                entered.TrySetResult();
                return await answer.Task.WaitAsync(token);
            }, (_, accepted, _) => { sent.TrySetResult(accepted); return Task.CompletedTask; }, connection.Token);
            approval.Queue(claim, "owner@example.test", "Browser");
            approval.Queue(claim, "owner@example.test", "Browser"); // Pending duplicate must not open another UI.
            if (mode == "bad-proof")
            {
                Check(!await sent.Task.WaitAsync(TimeSpan.FromSeconds(3)), "Bad proof was accepted.");
                Check(prompts == 0, "An invalid claim opened an approval prompt.");
            }
            else
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(store.LoadClientKey(clientId) is null && manager.PendingDecisions().Count == 0,
                    "Waiting for OK granted access.");
                // Queue returned while the owner answer is still pending. ReceiveLoop can handle heartbeats.
                Check(!sent.Task.IsCompleted && prompts == 1, "Approval ran synchronously or twice.");
                if (mode == "disconnect") connection.Cancel();
                else
                {
                    if (mode == "expired") clock.Now = clock.Now.AddMinutes(6);
                    answer.TrySetResult(mode != "cancel");
                    var accepted = await sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(accepted == (mode == "ok"), "Wrong approval result: " + mode);
                }
            }
            connection.Cancel();
            await approval.DisposeAsync();
            if (mode == "ok")
            {
                var key = store.LoadClientKey(clientId)!;
                var browserKey = VoicePairingCrypto.DeriveClientKey(browser, offer.DevicePublicKey, secret, pairId, clientId);
                Check(key is not null && CryptographicOperations.FixedTimeEquals(key, browserKey), "Approved browser key differs.");
                CryptographicOperations.ZeroMemory(key!);
                CryptographicOperations.ZeroMemory(browserKey);
            }
            else Check(store.LoadClientKey(clientId) is null, "Rejected request retained a client key: " + mode);
            if (mode == "disconnect") Check(manager.VerifyClaim(claim).AlreadyApproved == false,
                "Disconnected pending request became approved.");
            CryptographicOperations.ZeroMemory(secret);
        }
        finally { DeleteFixture(root); }
    }

    private static Task LegacyDecisionAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "codexvoice-approval-test-" + Guid.NewGuid().ToString("N")));
        try
        {
            var store = new VoicePairingStore(root);
            var decision = new VoicePendingPairDecision(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), "", "", DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeSeconds());
            using var client = VoicePairingCrypto.CreateDeviceKey();
            decision = decision with { ClientPublicKey = VoicePairingCrypto.EncodeBase64Url(VoicePairingCrypto.ExportPublicKey(client)),
                Proof = VoicePairingCrypto.EncodeBase64Url(new byte[32]) };
            store.SaveClientKey(decision.ClientId, new byte[32]);
            // A pre-upgrade durable decision is protected correctly, but carries no human consent.
            var method = typeof(VoicePairingStore).GetMethod("TryWriteNew", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            method.Invoke(store, new object[] { Path.Combine(root, "decision-" + decision.EventId + ".dpapi"),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(decision), "decision\0" + decision.EventId });
            Check(store.ListDecisions(DateTimeOffset.UtcNow).Count == 0 && store.LoadClientKey(decision.ClientId) is null,
                "A legacy automatic decision was replayed as owner approval.");
        }
        finally { DeleteFixture(root); }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void DeleteFixture(string root)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(root).StartsWith("codexvoice-approval-test-", StringComparison.Ordinal)
            && Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
