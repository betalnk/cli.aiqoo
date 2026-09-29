using System.Text;

namespace CodexVoice;

internal static class CliHistoryCryptoSelfTests
{
    internal static void Run()
    {
        // Independent Node.js crypto fixture; catches byte-order, AAD and tag drift
        // between this desktop and browser WebCrypto implementations.
        const string deviceId = "0123456789abcdef0123456789abcdef";
        const string clientId = "11111111111111111111111111111111";
        const string requestId = "22222222222222222222222222222222";
        var key = Enumerable.Repeat((byte)7, 32).ToArray();
        var nonce = Enumerable.Repeat((byte)9, 12).ToArray();
        var plain = Encoding.UTF8.GetBytes("{\"kind\":\"threads\",\"threads\":[],\"capturedAt\":1}");
        var encrypted = CliHistoryCrypto.Encrypt(key, deviceId, clientId, requestId,
            "threads", null, 1, plain, nonce);
        if (encrypted.Nonce != "CQkJCQkJCQkJCQkJ"
            || encrypted.Ciphertext !=
            "XKfv_dCU41uCFqNKg4vHrPacQYdcnJKhC91knI4pLViwhJe3lyBPdD7Hhyx1Sf5GO8CZBWzdp7bkJSk-HSc")
            throw new InvalidOperationException("CLI history AES-GCM fixture changed.");
        var aad = CliHistoryCrypto.BuildAad(deviceId, clientId, requestId, "threads", null, 1);
        if (!Convert.ToHexString(aad).ToLowerInvariant().EndsWith("74687265616473000031",
                StringComparison.Ordinal))
            throw new InvalidOperationException("CLI history AAD layout changed.");
    }
}
