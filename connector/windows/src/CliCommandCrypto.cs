using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace CodexVoice;

internal sealed record CliCommandCiphertext(string Nonce, string Ciphertext);

/// <summary>Separate AES-GCM domain for ephemeral phone commands.</summary>
internal static class CliCommandCrypto
{
    private const string Prefix = "CLI Voice command v1\0";
    private const int TagBytes = 16;
    private const int MaxPlaintextBytes = 9216;

    internal static CliCommandCiphertext Encrypt(ReadOnlySpan<byte> key,
        string deviceId, string clientId, string requestId, string op, string? threadId,
        string direction, string frame, int seq, string stage, ReadOnlySpan<byte> plaintext)
        => EncryptWithNonce(key, deviceId, clientId, requestId, op, threadId,
            direction, frame, seq, stage, plaintext, RandomNumberGenerator.GetBytes(12));

    // Deterministic entry point exists only for cross-language protocol fixtures.
    internal static CliCommandCiphertext EncryptWithNonce(ReadOnlySpan<byte> key,
        string deviceId, string clientId, string requestId, string op, string? threadId,
        string direction, string frame, int seq, string stage, ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> suppliedNonce)
    {
        if (key.Length != 32 || plaintext.Length > MaxPlaintextBytes || suppliedNonce.Length != 12)
            throw new ArgumentException("Invalid command key or plaintext size.");
        var aad = BuildAad(deviceId, clientId, requestId, op, threadId,
            direction, frame, seq, stage);
        var nonce = suppliedNonce.ToArray();
        var combined = new byte[plaintext.Length + TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, combined.AsSpan(0, plaintext.Length),
                combined.AsSpan(plaintext.Length, TagBytes), aad);
            return new CliCommandCiphertext(VoicePairingCrypto.EncodeBase64Url(nonce),
                VoicePairingCrypto.EncodeBase64Url(combined));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    internal static byte[] Decrypt(ReadOnlySpan<byte> key,
        string deviceId, string clientId, string requestId, string op, string? threadId,
        string direction, string frame, int seq, string stage,
        string nonceText, string ciphertextText, int maxPlaintextBytes)
    {
        if (key.Length != 32 || maxPlaintextBytes is < 0 or > MaxPlaintextBytes)
            throw new ArgumentException("Invalid command key or plaintext limit.");
        var aad = BuildAad(deviceId, clientId, requestId, op, threadId,
            direction, frame, seq, stage);
        var nonce = VoicePairingCrypto.DecodeBase64Url(nonceText, 12);
        var combined = VoicePairingCrypto.DecodeBase64Url(ciphertextText,
            maxPlaintextBytes + TagBytes);
        try
        {
            if (nonce.Length != 12 || combined.Length < TagBytes)
                throw new CryptographicException("Invalid command envelope size.");
            var plaintext = new byte[combined.Length - TagBytes];
            try
            {
                using var aes = new AesGcm(key, TagBytes);
                aes.Decrypt(nonce, combined.AsSpan(0, plaintext.Length),
                    combined.AsSpan(plaintext.Length, TagBytes), plaintext, aad);
                return plaintext;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    internal static byte[] BuildAad(string deviceId, string clientId, string requestId,
        string op, string? threadId, string direction, string frame, int seq, string stage)
    {
        VoicePairingCrypto.ValidateHexId(deviceId, nameof(deviceId));
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        VoicePairingCrypto.ValidateHexId(requestId, nameof(requestId));
        if (op is not ("send_text" or "transcribe_audio")
            || direction is not ("browser" or "device")
            || frame is not ("start" or "chunk" or "ack")
            || seq < 0 || seq > 528 || stage is null
            || (frame == "start" && (seq != 0 || stage.Length != 0 || direction != "browser"))
            || (frame == "chunk" && (seq == 0 || stage.Length != 0 || direction != "browser"))
            || (frame == "ack" && (seq is < 1 or > 3 || direction != "device"
                || stage is not ("ready" or "rejected" or "transcribed" or "submitted" or "confirmed" or "unknown"))))
            throw new ArgumentException("Invalid command AAD domain.");
        if (op == "send_text")
        {
            if (threadId is null || !SessionResolver.TryCanonicalThreadId(threadId, out _))
                throw new ArgumentException("Expected an exact Codex thread ID.", nameof(threadId));
        }
        else if (threadId is not null)
            throw new ArgumentException("Audio transcription has no thread ID.", nameof(threadId));
        return Encoding.UTF8.GetBytes(Prefix + deviceId + "\0" + clientId + "\0"
            + requestId + "\0" + op + "\0" + (threadId ?? "") + "\0"
            + direction + "\0" + frame + "\0"
            + seq.ToString(CultureInfo.InvariantCulture) + "\0" + stage);
    }
}
