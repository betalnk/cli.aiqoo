using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

internal sealed record VoiceDeviceRegistration(string DeviceId, string DeviceToken);
internal sealed record VoicePendingPairSecret(string PairId, byte[] Secret, long ExpiresAtUnixSeconds);
internal sealed record VoicePendingPairDecision(string EventId, string PairId, string ClientId,
    string ClientPublicKey, string Proof, long ExpiresAtUnixSeconds, bool LocallyApproved = false);

/// <summary>Windows-user-protected device identity, relay credential, and per-client AES keys.</summary>
internal sealed class VoicePairingStore
{
    private const int MaxProtectedBytes = 16 * 1024;
    private readonly string _root;

    internal VoicePairingStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexVoice", "cli-pairing-v1");
    }

    internal ECDiffieHellman LoadOrCreateDeviceKey()
    {
        var path = Path.Combine(_root, "device-key.dpapi");
        if (File.Exists(path)) return LoadDeviceKey(path);
        if (File.Exists(Path.Combine(_root, "device-registration.dpapi")))
            throw new CryptographicException("Device key is missing for an existing relay registration.");

        using var created = VoicePairingCrypto.CreateDeviceKey();
        var privateKey = created.ExportPkcs8PrivateKey();
        try
        {
            if (TryWriteNew(path, privateKey, "device-key"))
            {
                var key = ECDiffieHellman.Create();
                key.ImportPkcs8PrivateKey(privateKey, out var consumed);
                if (consumed != privateKey.Length) throw new CryptographicException("Invalid new device key.");
                return key;
            }
            return LoadDeviceKey(path);
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    internal void SaveDeviceRegistration(string deviceId, string token)
    {
        VoicePairingCrypto.ValidateHexId(deviceId, nameof(deviceId));
        ValidateDeviceToken(token);
        if (!File.Exists(Path.Combine(_root, "device-key.dpapi")))
            throw new CryptographicException("Device key must exist before relay registration.");
        var value = JsonSerializer.SerializeToUtf8Bytes(new VoiceDeviceRegistration(deviceId, token));
        try
        {
            var path = Path.Combine(_root, "device-registration.dpapi");
            if (File.Exists(path))
            {
                var existing = LoadDeviceRegistration();
                if (existing?.DeviceId == deviceId && existing.DeviceToken == token) return;
                throw new CryptographicException("Device registration already exists with a different identity.");
            }
            if (!TryWriteNew(path, value, "device-registration"))
            {
                var existing = LoadDeviceRegistration();
                if (existing?.DeviceId != deviceId || existing.DeviceToken != token)
                    throw new CryptographicException("Device registration changed concurrently.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(value); }
    }

    internal VoiceDeviceRegistration? LoadDeviceRegistration()
    {
        var path = Path.Combine(_root, "device-registration.dpapi");
        if (!File.Exists(path)) return null;
        var value = ReadProtected(path, "device-registration");
        try
        {
            var registration = JsonSerializer.Deserialize<VoiceDeviceRegistration>(value)
                ?? throw new CryptographicException("Stored device registration is empty.");
            VoicePairingCrypto.ValidateHexId(registration.DeviceId, nameof(registration.DeviceId));
            ValidateDeviceToken(registration.DeviceToken);
            return registration;
        }
        catch (JsonException exception)
        {
            throw new CryptographicException("Stored device registration is invalid.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(value); }
    }

    /// <summary>Allows a user-initiated new pair after the server revoked this exact device token.</summary>
    internal bool ForgetRevokedRegistration(VoiceDeviceRegistration expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var current = LoadDeviceRegistration();
        if (current is null || current != expected) return false;
        File.Delete(Path.Combine(_root, "device-registration.dpapi"));
        return true;
    }

    private static void ValidateDeviceToken(string token)
    {
        if (token is null || token.Length is < 40 or > 128 || !token.StartsWith("dvc_", StringComparison.Ordinal) ||
            token.Any(c => !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c is '_' or '-')))
            throw new ArgumentException("Invalid device token.", nameof(token));
    }

    internal void SaveClientKey(string clientId, ReadOnlySpan<byte> key)
    {
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        if (key.Length != VoicePairingCrypto.KeyBytes)
            throw new ArgumentException("Expected a 32-byte client key.", nameof(key));
        var path = ClientPath(clientId);
        if (File.Exists(path))
        {
            RequireMatchingExistingClientKey(clientId, key);
            return;
        }
        var copy = key.ToArray();
        try
        {
            if (!TryWriteNew(path, copy, "client-key\0" + clientId))
                RequireMatchingExistingClientKey(clientId, key);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    internal byte[]? LoadClientKey(string clientId)
    {
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        var path = ClientPath(clientId);
        if (!File.Exists(path)) return null;
        var key = ReadProtected(path, "client-key\0" + clientId);
        if (key.Length == VoicePairingCrypto.KeyBytes) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("Stored client key has an invalid length.");
    }

    internal void RemoveClientKey(string clientId)
    {
        VoicePairingCrypto.ValidateHexId(clientId, nameof(clientId));
        var path = ClientPath(clientId);
        if (File.Exists(path)) File.Delete(path);
    }

    internal void SavePendingPair(string pairId, ReadOnlySpan<byte> secret, DateTimeOffset expiresAt)
    {
        VoicePairingCrypto.ValidateHexId(pairId, nameof(pairId));
        if (secret.Length != VoicePairingCrypto.SecretBytes)
            throw new ArgumentException("Invalid QR secret length.", nameof(secret));
        var path = PendingPath(pairId);
        var secretCopy = secret.ToArray();
        byte[]? value = null;
        try
        {
            value = JsonSerializer.SerializeToUtf8Bytes(new VoicePendingPairSecret(
                pairId, secretCopy, expiresAt.ToUnixTimeSeconds()));
            if (!TryWriteNew(path, value, "pending-pair\0" + pairId))
                throw new CryptographicException("A pending CLI pair with this ID already exists.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretCopy);
            if (value is not null) CryptographicOperations.ZeroMemory(value);
        }
    }

    internal VoicePendingPairSecret? LoadPendingPair(string pairId, DateTimeOffset now)
    {
        VoicePairingCrypto.ValidateHexId(pairId, nameof(pairId));
        var path = PendingPath(pairId);
        if (!File.Exists(path)) return null;
        var value = ReadProtected(path, "pending-pair\0" + pairId);
        try
        {
            var pending = JsonSerializer.Deserialize<VoicePendingPairSecret>(value)
                ?? throw new CryptographicException("Pending CLI pair is empty.");
            if (pending.PairId != pairId || pending.Secret?.Length != VoicePairingCrypto.SecretBytes)
                throw new CryptographicException("Pending CLI pair is invalid.");
            if (pending.ExpiresAtUnixSeconds <= now.ToUnixTimeSeconds())
            {
                CryptographicOperations.ZeroMemory(pending.Secret);
                File.Delete(path);
                return null;
            }
            return pending;
        }
        finally { CryptographicOperations.ZeroMemory(value); }
    }

    internal void DeletePendingPair(string pairId)
    {
        VoicePairingCrypto.ValidateHexId(pairId, nameof(pairId));
        var path = PendingPath(pairId);
        if (File.Exists(path)) File.Delete(path);
    }

    internal void SaveDecision(VoicePendingPairDecision decision)
    {
        ValidateDecision(decision);
        if (!decision.LocallyApproved)
            throw new CryptographicException("A CLI pairing decision requires local approval.");
        var path = DecisionPath(decision.EventId);
        var value = JsonSerializer.SerializeToUtf8Bytes(decision);
        try
        {
            if (TryWriteNew(path, value, "decision\0" + decision.EventId)) return;
            if (LoadDecision(decision.EventId, DateTimeOffset.UtcNow) != decision)
                throw new CryptographicException("The CLI pairing event changed after acceptance.");
        }
        finally { CryptographicOperations.ZeroMemory(value); }
    }

    internal VoicePendingPairDecision? LoadDecision(string eventId, DateTimeOffset now)
    {
        VoicePairingCrypto.ValidateHexId(eventId, nameof(eventId));
        var path = DecisionPath(eventId);
        if (!File.Exists(path)) return null;
        var value = ReadProtected(path, "decision\0" + eventId);
        try
        {
            var decision = JsonSerializer.Deserialize<VoicePendingPairDecision>(value)
                ?? throw new CryptographicException("CLI pairing decision is empty.");
            ValidateDecision(decision);
            if (decision.EventId != eventId)
                throw new CryptographicException("CLI pairing decision ID changed.");
            // Old builds wrote automatic decisions. They are not evidence of owner consent.
            if (!decision.LocallyApproved || decision.ExpiresAtUnixSeconds <= now.ToUnixTimeSeconds())
            {
                // An approved decision may have reached the server even if its ACK was
                // lost. Expiry ends decision replay, not that already active client's key.
                if (!decision.LocallyApproved) RemoveClientKey(decision.ClientId);
                File.Delete(path);
                return null;
            }
            return decision;
        }
        finally { CryptographicOperations.ZeroMemory(value); }
    }

    internal IReadOnlyList<VoicePendingPairDecision> ListDecisions(DateTimeOffset now)
    {
        if (!Directory.Exists(_root)) return [];
        var decisions = new List<VoicePendingPairDecision>();
        foreach (var path in Directory.EnumerateFiles(_root, "decision-*.dpapi", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var id = name["decision-".Length..];
            if (id.Length != 32 || id.Any(character =>
                    !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
                continue;
            var decision = LoadDecision(id, now);
            if (decision is not null) decisions.Add(decision);
        }
        return decisions;
    }

    internal void DeleteDecision(string eventId)
    {
        VoicePairingCrypto.ValidateHexId(eventId, nameof(eventId));
        var path = DecisionPath(eventId);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PendingPath(string pairId) => Path.Combine(_root, "pending-" + pairId + ".dpapi");
    private string DecisionPath(string eventId) => Path.Combine(_root, "decision-" + eventId + ".dpapi");

    private static void ValidateDecision(VoicePendingPairDecision decision)
    {
        VoicePairingCrypto.ValidateHexId(decision.EventId, nameof(decision.EventId));
        VoicePairingCrypto.ValidateHexId(decision.PairId, nameof(decision.PairId));
        VoicePairingCrypto.ValidateHexId(decision.ClientId, nameof(decision.ClientId));
        if (decision.ExpiresAtUnixSeconds <= 0 || decision.Proof is null
            || decision.ClientPublicKey is null)
            throw new CryptographicException("CLI pairing decision is invalid.");
        using var key = VoicePairingCrypto.ImportPublicKey(decision.ClientPublicKey);
        if (VoicePairingCrypto.DecodeBase64Url(decision.Proof, 64).Length != 32)
            throw new CryptographicException("CLI pairing proof has an invalid length.");
    }

    private ECDiffieHellman LoadDeviceKey(string path)
    {
        var privateKey = ReadProtected(path, "device-key");
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportPkcs8PrivateKey(privateKey, out var consumed);
            if (consumed != privateKey.Length) throw new CryptographicException("Invalid device key data.");
            VoicePairingCrypto.RequireP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
    }

    private void RequireMatchingExistingClientKey(string clientId, ReadOnlySpan<byte> key)
    {
        var existing = LoadClientKey(clientId)
            ?? throw new CryptographicException("Stored client key disappeared.");
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(existing, key))
                throw new CryptographicException("A different key already exists for this client.");
        }
        finally { CryptographicOperations.ZeroMemory(existing); }
    }

    private string ClientPath(string clientId)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(clientId));
        return Path.Combine(_root, "client-" + Convert.ToHexString(hash).ToLowerInvariant() + ".dpapi");
    }

    private bool TryWriteNew(string path, byte[] clearBytes, string purpose)
    {
        Directory.CreateDirectory(_root);
        var protectedBytes = WindowsUserDpapi.Protect(clearBytes, purpose);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporaryPath, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { return false; }
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static byte[] ReadProtected(string path, string purpose)
    {
        var info = new FileInfo(path);
        if (info.Length is < 1 or > MaxProtectedBytes)
            throw new CryptographicException("Protected key file has an invalid size.");
        var protectedBytes = File.ReadAllBytes(path);
        try { return WindowsUserDpapi.Unprotect(protectedBytes, purpose); }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
    }
}

/// <summary>Native DPAPI avoids an additional NuGet package in this desktop application.</summary>
internal static class WindowsUserDpapi
{
    private const uint UiForbidden = 0x1;

    internal static byte[] Protect(byte[] clearBytes, string purpose) => Transform(clearBytes, purpose, true);
    internal static byte[] Unprotect(byte[] protectedBytes, string purpose) => Transform(protectedBytes, purpose, false);

    private static byte[] Transform(byte[] input, string purpose, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI requires Windows.");
        var entropy = Encoding.UTF8.GetBytes("CLI Voice DPAPI v1\0" + purpose);
        using var inputBlob = PinnedBlob.From(input);
        using var entropyBlob = PinnedBlob.From(entropy);
        NativeBlob output;
        var ok = protect
            ? CryptProtectData(ref inputBlob.Value, "CLI Voice pairing v1",
                ref entropyBlob.Value, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
            : CryptUnprotectData(ref inputBlob.Value, IntPtr.Zero,
                ref entropyBlob.Value, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
        if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
        try
        {
            if (output.Size is < 1 or > 16 * 1024)
                throw new CryptographicException("DPAPI output has an invalid size.");
            var bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlob
    {
        internal int Size;
        internal IntPtr Data;
    }

    private sealed class PinnedBlob : IDisposable
    {
        internal NativeBlob Value;

        private PinnedBlob(byte[] data)
        {
            Value.Size = data.Length;
            Value.Data = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, Value.Data, data.Length);
        }

        internal static PinnedBlob From(byte[] data) => new(data);

        public void Dispose()
        {
            if (Value.Data == IntPtr.Zero) return;
            for (var i = 0; i < Value.Size; i++) Marshal.WriteByte(Value.Data, i, 0);
            Marshal.FreeHGlobal(Value.Data);
            Value = default;
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref NativeBlob input, string description,
        ref NativeBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out NativeBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref NativeBlob input, IntPtr description,
        ref NativeBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out NativeBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
