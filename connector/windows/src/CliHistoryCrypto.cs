using System.Security.Cryptography;
using System.Text;

namespace CodexVoice;

/// <summary>Pair-key encryption for the private CLI history relay. The relay sees only routing metadata.</summary>
internal static class CliHistoryCrypto
{
    private const int MaxPlaintextBytes = 32 * 1024;

    internal static (string Nonce, string Ciphertext) Encrypt(byte[] key, string deviceId,
        string clientId, string requestId, string kind, string? threadId, int sequence,
        ReadOnlySpan<byte> plaintext, byte[]? suppliedNonce = null)
    {
        if (key.Length != VoicePairingCrypto.KeyBytes || plaintext.Length > MaxPlaintextBytes
            || sequence < 1)
            throw new ArgumentException("Invalid history encryption input.");
        var nonce = suppliedNonce ?? RandomNumberGenerator.GetBytes(12);
        if (nonce.Length != 12) throw new ArgumentException("History nonce must be 12 bytes.");
        var aad = BuildAad(deviceId, clientId, requestId, kind, threadId, sequence);
        var combined = new byte[plaintext.Length + 16];
        using (var cipher = new AesGcm(key, 16))
            cipher.Encrypt(nonce, plaintext, combined.AsSpan(0, plaintext.Length),
                combined.AsSpan(plaintext.Length, 16), aad);
        return (VoicePairingCrypto.EncodeBase64Url(nonce),
            VoicePairingCrypto.EncodeBase64Url(combined));
    }

    internal static byte[] BuildAad(string deviceId, string clientId, string requestId,
        string kind, string? threadId, int sequence)
    {
        VoicePairingCrypto.ValidateHexId(deviceId, nameof(deviceId));
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        VoicePairingCrypto.ValidateHexId(requestId, nameof(requestId));
        if (kind is not ("threads" or "thread") || sequence < 1)
            throw new ArgumentException("Invalid history request.");
        if (kind == "thread")
        {
            if (!SessionResolver.TryCanonicalThreadId(threadId, out var canonical)
                || canonical != threadId) throw new ArgumentException("Invalid Codex thread ID.");
        }
        else if (threadId is not null)
            throw new ArgumentException("Thread list must not include a thread ID.");
        return Encoding.UTF8.GetBytes("CLI Voice history v1\0" + deviceId + "\0" +
            clientId + "\0" + requestId + "\0" + kind + "\0" +
            (threadId ?? "") + "\0" + sequence);
    }
}
