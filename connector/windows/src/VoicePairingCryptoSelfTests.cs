using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

/// <summary>Offline browser interop vectors from relay PROTOCOL.md; no network or live CLI access.</summary>
internal static class VoicePairingCryptoSelfTests
{
    private const string PairId = "0123456789abcdef0123456789abcdef";
    private const string ClientId = "fedcba9876543210fedcba9876543210";
    private const string DeviceId = "00112233445566778899aabbccddeeff";
    private const string RequestId = "123e4567-e89b-42d3-a456-426614174000";
    private const string DeviceSpki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEaxfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpZP40Li_hp_m47n60p8D54WK84zV2sxXs7LtkBoN79R9Q";
    private const string BrowserSpki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfPJ7GI0DT36KUjgDBLUaw8CJaeJ38hs1pgtI_EdmmXgHd1UQ247QQCk9msafdDDbun2t5jzpgimeBLedInhz0Q";
    private const string Fingerprint = "XNJS-wzokyQ2-vjM0QQJgbie5K1rn-niorfnGqyyfNM";
    private const string Proof = "T9hV6mrVAFPBVHlb6i-LGwdaHAQMnn8mzvyPpcEVW4o";
    private const string Shared = "fPJ7GI0DT36KUjgDBLUaw8CJaeJ38hs1pgtI_EdmmXg";
    private const string AesKey = "aBQtSFcGpDM3EhpHo5Uesow-0wAxs-SetqGF6OwDQp8";
    private const string CiphertextAndTag = "byK2gteamDwnQRjcLZb06jRjAmmqj-2U5yKJxBbVYPzAuIUtz5EDlBY";

    internal static void Run()
    {
        var pairSecret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var spki = VoicePairingCrypto.DecodeBase64Url(DeviceSpki, 256);
        var browserSpki = VoicePairingCrypto.DecodeBase64Url(BrowserSpki, 256);
        using var device = CreateDeterministicKey(DeviceSpki, 1);
        using var browser = CreateDeterministicKey(BrowserSpki, 2);
        Equal(DeviceSpki, VoicePairingCrypto.EncodeBase64Url(VoicePairingCrypto.ExportPublicKey(device)));
        Equal(BrowserSpki, VoicePairingCrypto.EncodeBase64Url(VoicePairingCrypto.ExportPublicKey(browser)));
        Equal(Fingerprint, VoicePairingCrypto.Fingerprint(spki));
        Equal(Shared, VoicePairingCrypto.EncodeBase64Url(device.DeriveRawSecretAgreement(browser.PublicKey)));

        var proof = VoicePairingCrypto.CreateProof(pairSecret, PairId, browserSpki);
        Equal(Proof, VoicePairingCrypto.EncodeBase64Url(proof));
        if (!VoicePairingCrypto.VerifyProof(pairSecret, PairId, browserSpki, Proof) ||
            VoicePairingCrypto.VerifyProof(pairSecret, PairId, browserSpki,
                Proof[..^1] + (Proof[^1] == 'A' ? 'B' : 'A')))
            throw new InvalidOperationException("Pairing proof validation failed.");

        var key = VoicePairingCrypto.DeriveClientKey(device, BrowserSpki, pairSecret, PairId, ClientId);
        Equal(AesKey, VoicePairingCrypto.EncodeBase64Url(key));
        var browserKey = VoicePairingCrypto.DeriveClientKey(browser, DeviceSpki, pairSecret, PairId, ClientId);
        if (!CryptographicOperations.FixedTimeEquals(key, browserKey))
            throw new InvalidOperationException("ECDH directions derived different keys.");

        var nonce = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        var plaintext = Encoding.UTF8.GetBytes("{\"kind\":\"ping\",\"value\":1}");
        var envelope = VoicePairingCrypto.EncryptWithNonce(key, DeviceId, ClientId,
            RequestId, "browser", plaintext, nonce);
        using (var json = JsonDocument.Parse(VoicePairingCrypto.DecodeBase64Url(envelope, 50 * 1024)))
        {
            Equal(RequestId, json.RootElement.GetProperty("requestId").GetString());
            Equal(CiphertextAndTag, json.RootElement.GetProperty("ciphertext").GetString());
        }
        var replay = new VoiceReplayGuard();
        var decrypted = VoicePairingCrypto.Decrypt(key, DeviceId, ClientId,
            RequestId, "browser", envelope, replay);
        Equal(RequestId, VoicePairingCrypto.ReadRequestId(envelope));
        if (decrypted.IsReplay || !CryptographicOperations.FixedTimeEquals(plaintext, decrypted.Plaintext))
            throw new InvalidOperationException("AES-GCM plaintext changed.");
        if (!VoicePairingCrypto.Decrypt(key, DeviceId, ClientId,
                RequestId, "browser", envelope, replay).IsReplay)
            throw new InvalidOperationException("AES-GCM replay was accepted twice.");
        Throws<CryptographicException>(() => VoicePairingCrypto.Decrypt(
            key, DeviceId, ClientId, RequestId, "device", envelope, new VoiceReplayGuard()));

        var qr = VoicePairingCrypto.BuildQrUrl(PairId, "2345-6789-ABCD", pairSecret, spki);
        Equal("https://cli.aiqoo.ru/pair.html#v=1&pairId=" + PairId +
              "&code=23456789ABCD&secret=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8&fp=" + Fingerprint, qr);
        Throws<ArgumentException>(() => VoicePairingCrypto.NormalizeCode("0123-4567-ABCD"));
        Throws<FormatException>(() => VoicePairingCrypto.DecodeBase64Url("AA==", 32));

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(browserKey);
        CryptographicOperations.ZeroMemory(pairSecret);
        CryptographicOperations.ZeroMemory(proof);
    }

    private static ECDiffieHellman CreateDeterministicKey(string publicSpki, byte scalar)
    {
        using var publicKey = VoicePairingCrypto.ImportPublicKey(publicSpki);
        var parameters = publicKey.ExportParameters(false);
        parameters.D = new byte[32];
        parameters.D[^1] = scalar;
        return ECDiffieHellman.Create(parameters);
    }

    private static void Equal(string expected, string? actual)
    {
        if (expected != actual)
            throw new InvalidOperationException("Browser interop vector mismatch.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected protocol rejection did not occur: " + typeof(T).Name);
    }
}
