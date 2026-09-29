using System.Security.Cryptography;

namespace CodexVoice;

internal static class VoicePairingRecoverySelfTests
{
    internal static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "codexvoice-pairing-test-" + Guid.NewGuid().ToString("N")));
        try
        {
            var store = new VoicePairingStore(root);
            const string pairId = "0123456789abcdef0123456789abcdef";
            const string eventId = "11112222333344445555666677778888";
            const string clientId = "aaaabbbbccccddddeeeeffff00001111";
            VoicePairingOffer offer;
            using (var beforeRestart = new VoicePairingManager(store))
                offer = beforeRestart.Begin(pairId, "2345-6789-ABCD",
                    DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());

            var encodedSecret = offer.QrUrl.Split('&')
                .Single(part => part.StartsWith("secret=", StringComparison.Ordinal))["secret=".Length..];
            var secret = VoicePairingCrypto.DecodeBase64Url(encodedSecret, 32);
            using var clientKey = VoicePairingCrypto.CreateDeviceKey();
            var clientSpki = VoicePairingCrypto.ExportPublicKey(clientKey);
            var publicKey = VoicePairingCrypto.EncodeBase64Url(clientSpki);
            var proof = VoicePairingCrypto.EncodeBase64Url(
                VoicePairingCrypto.CreateProof(secret, pairId, clientSpki));
            var claim = new VoicePairingClaim(eventId, pairId, clientId, publicKey, proof);

            // The in-memory QR secret is gone. The protected local copy must still
            // verify the browser proof after a process restart.
            using (var afterRestart = new VoicePairingManager(store))
            {
                afterRestart.Accept(claim);
                if (afterRestart.PendingDecisions().Single().EventId != eventId)
                    throw new InvalidOperationException("Accepted CLI pair was not durable.");
            }
            using (var afterLostAck = new VoicePairingManager(store))
            {
                afterLostAck.Accept(claim);
                if (afterLostAck.PendingDecisions().Single().ClientId != clientId)
                    throw new InvalidOperationException("CLI accept decision was not replayable.");
                afterLostAck.ConfirmDecision(eventId, pairId, clientId);
                if (afterLostAck.PendingDecisions().Count != 0
                    || store.LoadPendingPair(pairId, DateTimeOffset.UtcNow) is not null)
                    throw new InvalidOperationException("Acknowledged CLI pair was not cleared.");
            }
            var key = store.LoadClientKey(clientId)
                ?? throw new InvalidOperationException("Confirmed CLI pair lost its local key.");
            CryptographicOperations.ZeroMemory(key);
            var registration = new VoiceDeviceRegistration(
                "55556666777788889999aaaabbbbcccc", "dvc_" + new string('A', 43));
            store.SaveDeviceRegistration(registration.DeviceId, registration.DeviceToken);
            if (store.ForgetRevokedRegistration(registration with { DeviceId = pairId })
                || store.LoadDeviceRegistration() != registration
                || !store.ForgetRevokedRegistration(registration)
                || store.LoadDeviceRegistration() is not null)
                throw new InvalidOperationException("Revoked CLI registration recovery failed.");
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(clientSpki);
        }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("codexvoice-pairing-test-", StringComparison.Ordinal)
                && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
