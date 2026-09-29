using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

internal sealed record VoiceDecryptedEnvelope(string RequestId, byte[] Plaintext, bool IsReplay);

/// <summary>Bytes shared by the Windows device and the browser. Keep in sync with CLI pairing protocol.</summary>
internal static class VoicePairingCrypto
{
    internal const int SecretBytes = 32;
    internal const int KeyBytes = 32;
    internal const int PairingSeconds = 300;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    // The relay limits the base64url envelope to 64 KiB. Keep ample room for both encodings.
    private const int MaxPlaintextBytes = 32 * 1024;
    private const int MaxEnvelopeChars = 64 * 1024;
    private const string ProtocolPrefix = "CLI Voice v1\0";
    private const string ProofPrefix = "CLI Voice pair v1\0";
    private const string PairCodeAlphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string P256Oid = "1.2.840.10045.3.1.7";

    internal static ECDiffieHellman CreateDeviceKey() =>
        ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    internal static byte[] ExportPublicKey(ECDiffieHellman key)
    {
        RequireP256(key);
        return key.ExportSubjectPublicKeyInfo();
    }

    internal static ECDiffieHellman ImportPublicKey(string encoded)
    {
        var der = DecodeBase64Url(encoded, 256);
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(der, out var consumed);
            if (consumed != der.Length || !CryptographicOperations.FixedTimeEquals(der, key.ExportSubjectPublicKeyInfo()))
                throw new CryptographicException("Non-canonical P-256 public key.");
            RequireP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    internal static void RequireP256(ECDiffieHellman key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != P256Oid)
            throw new CryptographicException("A P-256 key is required.");
    }

    internal static string Fingerprint(ReadOnlySpan<byte> desktopSpki) =>
        EncodeBase64Url(SHA256.HashData(desktopSpki));

    internal static byte[] CreatePairSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    internal static byte[] CreateProof(ReadOnlySpan<byte> pairSecret, string pairId, ReadOnlySpan<byte> clientSpki)
    {
        RequirePairSecret(pairSecret);
        ValidateHexId(pairId, nameof(pairId));
        if (clientSpki.Length is < 70 or > 256)
            throw new ArgumentException("Invalid client public key size.", nameof(clientSpki));
        var prefix = Encoding.UTF8.GetBytes(ProofPrefix + pairId + "\0");
        var message = new byte[prefix.Length + clientSpki.Length];
        prefix.CopyTo(message, 0);
        clientSpki.CopyTo(message.AsSpan(prefix.Length));
        try { return HMACSHA256.HashData(pairSecret, message); }
        finally { CryptographicOperations.ZeroMemory(message); }
    }

    internal static bool VerifyProof(ReadOnlySpan<byte> pairSecret, string pairId,
        ReadOnlySpan<byte> clientSpki, string encodedProof)
    {
        byte[] supplied;
        try { supplied = DecodeBase64Url(encodedProof, 64); }
        catch (FormatException) { return false; }
        if (supplied.Length != 32)
        {
            CryptographicOperations.ZeroMemory(supplied);
            return false;
        }
        var expected = CreateProof(pairSecret, pairId, clientSpki);
        try { return CryptographicOperations.FixedTimeEquals(expected, supplied); }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(supplied);
        }
    }

    internal static byte[] DeriveClientKey(ECDiffieHellman desktopKey, string clientSpki,
        ReadOnlySpan<byte> pairSecret, string pairId, string clientId)
    {
        RequireP256(desktopKey);
        RequirePairSecret(pairSecret);
        ValidateHexId(pairId, nameof(pairId));
        ValidateHexId(clientId, nameof(clientId));
        using var clientKey = ImportPublicKey(clientSpki);
        var rawSecret = desktopKey.DeriveRawSecretAgreement(clientKey.PublicKey);
        var info = Encoding.UTF8.GetBytes(ProtocolPrefix + pairId + "\0" + clientId);
        var salt = pairSecret.ToArray();
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, rawSecret, KeyBytes, salt, info); }
        finally
        {
            CryptographicOperations.ZeroMemory(rawSecret);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    internal static string Encrypt(ReadOnlySpan<byte> key, string deviceId, string clientId,
        string requestId, string direction, ReadOnlySpan<byte> plaintext) =>
        EncryptWithNonce(key, deviceId, clientId, requestId, direction,
            plaintext, RandomNumberGenerator.GetBytes(NonceBytes));

    // The explicit nonce entry point is only used by deterministic cross-language tests.
    internal static string EncryptWithNonce(ReadOnlySpan<byte> key, string deviceId, string clientId,
        string requestId, string direction, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce)
    {
        RequireKey(key);
        if (nonce.Length != NonceBytes) throw new ArgumentException("Expected a 12-byte nonce.", nameof(nonce));
        if (plaintext.Length > MaxPlaintextBytes) throw new ArgumentOutOfRangeException(nameof(plaintext));
        var aad = BuildAad(deviceId, clientId, requestId, direction);
        var combined = new byte[plaintext.Length + TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plaintext, combined.AsSpan(0, plaintext.Length),
                combined.AsSpan(plaintext.Length, TagBytes), aad);
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            requestId,
            nonce = EncodeBase64Url(nonce),
            ciphertext = EncodeBase64Url(combined)
        });
        var envelope = EncodeBase64Url(json);
        if (envelope.Length > MaxEnvelopeChars)
            throw new ArgumentOutOfRangeException(nameof(plaintext), "Encrypted envelope exceeds the relay limit.");
        return envelope;
    }

    internal static string ReadRequestId(string encodedEnvelope)
    {
        if (encodedEnvelope is null || encodedEnvelope.Length > MaxEnvelopeChars)
            throw new FormatException("Encrypted envelope exceeds the relay limit.");
        var json = DecodeBase64Url(encodedEnvelope, 50 * 1024);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("requestId", out var request) ||
            request.ValueKind != JsonValueKind.String)
            throw new FormatException("Encrypted envelope has no request ID.");
        var requestId = request.GetString()!;
        ValidateRequestId(requestId);
        return requestId;
    }

    internal static VoiceDecryptedEnvelope Decrypt(ReadOnlySpan<byte> key, string deviceId, string clientId,
        string expectedRequestId, string direction, string encodedEnvelope, VoiceReplayGuard replayGuard)
    {
        RequireKey(key);
        ArgumentNullException.ThrowIfNull(replayGuard);
        ValidateRequestId(expectedRequestId);
        if (encodedEnvelope is null || encodedEnvelope.Length > MaxEnvelopeChars)
            throw new FormatException("Encrypted envelope exceeds the relay limit.");
        var json = DecodeBase64Url(encodedEnvelope, 50 * 1024);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
            !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number ||
            version.GetInt32() != 1 ||
            !root.TryGetProperty("requestId", out var request) || request.ValueKind != JsonValueKind.String ||
            request.GetString() != expectedRequestId ||
            !root.TryGetProperty("nonce", out var nonceElement) || nonceElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("ciphertext", out var ciphertextElement) || ciphertextElement.ValueKind != JsonValueKind.String)
            throw new CryptographicException("Invalid encrypted envelope.");

        var nonce = DecodeBase64Url(nonceElement.GetString()!, 32);
        var combined = DecodeBase64Url(ciphertextElement.GetString()!, MaxPlaintextBytes + TagBytes);
        if (nonce.Length != NonceBytes || combined.Length < TagBytes || combined.Length > MaxPlaintextBytes + TagBytes)
            throw new CryptographicException("Invalid encrypted envelope sizes.");
        var plaintext = new byte[combined.Length - TagBytes];
        var aad = BuildAad(deviceId, clientId, expectedRequestId, direction);
        using (var aes = new AesGcm(key, TagBytes))
            aes.Decrypt(nonce, combined.AsSpan(0, plaintext.Length),
                combined.AsSpan(plaintext.Length, TagBytes), plaintext, aad);
        var replay = !replayGuard.TryAccept(clientId, direction, expectedRequestId, nonce);
        return new VoiceDecryptedEnvelope(expectedRequestId, plaintext, replay);
    }

    internal static byte[] BuildAad(string deviceId, string clientId, string requestId, string direction)
    {
        ValidateHexId(deviceId, nameof(deviceId));
        ValidateHexId(clientId, nameof(clientId));
        ValidateRequestId(requestId);
        if (direction is not ("browser" or "device"))
            throw new ArgumentException("Direction must be browser or device.", nameof(direction));
        return Encoding.UTF8.GetBytes(ProtocolPrefix + deviceId + "\0" + clientId + "\0" +
            requestId + "\0" + direction);
    }

    internal static string BuildQrUrl(string pairId, string displayedCode, ReadOnlySpan<byte> pairSecret,
        ReadOnlySpan<byte> desktopSpki)
    {
        ValidateHexId(pairId, nameof(pairId));
        RequirePairSecret(pairSecret);
        var code = NormalizeCode(displayedCode);
        return "https://cli.aiqoo.ru/pair.html#v=1&pairId=" + pairId + "&code=" + code +
            "&secret=" + EncodeBase64Url(pairSecret) + "&fp=" + Fingerprint(desktopSpki);
    }

    internal static string NormalizeCode(string displayedCode)
    {
        ArgumentNullException.ThrowIfNull(displayedCode);
        var raw = displayedCode.Replace("-", "", StringComparison.Ordinal);
        if (raw.Length != 12 || raw.Any(c => !PairCodeAlphabet.Contains(c)))
            throw new ArgumentException("Expected a 12-character uppercase Crockford code.", nameof(displayedCode));
        if (displayedCode.Length != 12 &&
            (displayedCode.Length != 14 || displayedCode[4] != '-' || displayedCode[9] != '-'))
            throw new ArgumentException("Invalid pairing code grouping.", nameof(displayedCode));
        return raw;
    }

    internal static void ValidateHexId(string value, string name)
    {
        if (value is null || value.Length != 32 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new ArgumentException("Expected a 32-character lowercase hexadecimal ID.", name);
    }

    internal static void ValidateRequestId(string requestId)
    {
        if (requestId is null || !Guid.TryParseExact(requestId, "D", out var parsed) ||
            parsed.ToString("D") != requestId || "89ab".IndexOf(requestId[19]) < 0)
            throw new ArgumentException("Expected a lowercase RFC 4122 UUID request ID.", nameof(requestId));
    }

    internal static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] DecodeBase64Url(string encoded, int maxDecodedBytes)
    {
        if (encoded is null || encoded.Length > (maxDecodedBytes * 4L + 2) / 3 + 4 ||
            encoded.Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c is '-' or '_')))
            throw new FormatException("Invalid base64url text.");
        var standard = encoded.Replace('-', '+').Replace('_', '/');
        standard += new string('=', (4 - standard.Length % 4) % 4);
        byte[] decoded;
        try { decoded = Convert.FromBase64String(standard); }
        catch (FormatException) { throw new FormatException("Invalid base64url text."); }
        if (decoded.Length > maxDecodedBytes || EncodeBase64Url(decoded) != encoded)
            throw new FormatException("Non-canonical or oversized base64url text.");
        return decoded;
    }

    private static void RequirePairSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes) throw new ArgumentException("Expected a 32-byte pair secret.");
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes) throw new ArgumentException("Expected a 32-byte AES key.");
    }
}
