using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

internal static class CliCommandSelfTests
{
    private static readonly string DeviceId = new('1', 32);
    private static readonly string ClientId = new('2', 32);
    private static readonly string RequestId = new('3', 32);
    private const string ThreadId = "123e4567-e89b-42d3-a456-426614174000";

    internal static async Task RunAsync()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var nonce = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        var plain = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"op\":\"send_text\",\"threadId\":\"" + ThreadId
            + "\",\"totalBytes\":2,\"totalChunks\":1}");
        var aad = CliCommandCrypto.BuildAad(DeviceId, ClientId, RequestId,
            "send_text", ThreadId, "browser", "start", 0, "");
        Check(Encoding.UTF8.GetString(aad) == "CLI Voice command v1\0" + DeviceId
            + "\0" + ClientId + "\0" + RequestId + "\0send_text\0" + ThreadId
            + "\0browser\0start\00\0");
        var cipher = CliCommandCrypto.EncryptWithNonce(key, DeviceId, ClientId,
            RequestId, "send_text", ThreadId, "browser", "start", 0, "", plain, nonce);
        Check(cipher.Nonce == "AAECAwQFBgcICQoL");
        var decrypted = CliCommandCrypto.Decrypt(key, DeviceId, ClientId,
            RequestId, "send_text", ThreadId, "browser", "start", 0, "",
            cipher.Nonce, cipher.Ciphertext, 1024);
        Check(decrypted.AsSpan().SequenceEqual(plain));
        CryptographicOperations.ZeroMemory(decrypted);
        var badAadFailed = false;
        try
        {
            _ = CliCommandCrypto.Decrypt(key, DeviceId, ClientId,
                RequestId, "send_text", ThreadId, "browser", "chunk", 1, "",
                cipher.Nonce, cipher.Ciphertext, 1024);
        }
        catch (CryptographicException) { badAadFailed = true; }
        Check(badAadFailed);
        if (Environment.GetEnvironmentVariable("CLI_COMMAND_VECTOR") == "1")
            Console.WriteLine("COMMAND_VECTOR_CIPHERTEXT=" + cipher.Ciphertext);
        if (Environment.GetEnvironmentVariable("CLI_COMMAND_GUARD_PROBE") == "1")
            Console.WriteLine("COMMAND_INPUT_DESKTOP_READY=" + CliInteractiveDesktopGuard.IsReady());

        using var startJson = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "command_start", requestId = RequestId, deviceId = DeviceId,
            clientId = ClientId, op = "send_text", threadId = ThreadId,
            totalBytes = 2, totalChunks = 1, nonce = cipher.Nonce,
            ciphertext = cipher.Ciphertext
        }));
        Check(CliCommandProtocol.TryReadStart(startJson.RootElement, DeviceId,
            out var start) && start is not null);
        Check(CliCommandProtocol.ValidateMetadata(start!, plain, out _));
        Check(CliCommandProtocol.ExpectedChunkLength(start!, 1) == 2);
        Check(CliCommandProtocol.ExpectedChunkLength(start!, 2) == -1);
        Check(!CliCommandProtocol.ValidateMetadata(start!, Encoding.UTF8.GetBytes(
            "{\"v\":1,\"op\":\"send_text\",\"threadId\":\"" + ThreadId
            + "\",\"totalBytes\":2,\"totalChunks\":1,\"extra\":1}"), out _));
        Check(Encoding.UTF8.GetByteCount(CliCommandProtocol.CropTranscript(
            string.Concat(Enumerable.Repeat("Привет🙂", 2000)))) <= 8192);
        Check(CliCommandProtocol.CropTranscript("Коротко") == "Коротко");

        var audioStart = new CliCommandStart(RequestId, ClientId, "transcribe_audio",
            null, 4_320_000, 528, cipher.Nonce, cipher.Ciphertext);
        var audioMetadata = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"op\":\"transcribe_audio\",\"threadId\":null,\"totalBytes\":4320000,\"totalChunks\":528,\"format\":\"pcm_s16le\",\"sampleRate\":48000,\"channels\":1}");
        Check(CliCommandProtocol.ValidateMetadata(audioStart, audioMetadata, out var rate)
            && rate == 48000);
        Check(CliCommandProtocol.ExpectedChunkLength(audioStart, 528) == 2816);
        var tooSlowRate = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"op\":\"transcribe_audio\",\"threadId\":null,\"totalBytes\":4320000,\"totalChunks\":528,\"format\":\"pcm_s16le\",\"sampleRate\":8000,\"channels\":1}");
        Check(!CliCommandProtocol.ValidateMetadata(audioStart, tooSlowRate, out _));

        var testRoot = Path.Combine(Path.GetTempPath(), "cli-command-selftest-"
            + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new VoicePairingStore(testRoot);
            store.SaveClientKey(ClientId, key);
            var acks = new List<JsonElement>();
            await using var manager = new CliCommandManager(store, DeviceId,
                (message, _) =>
                {
                    acks.Add(JsonSerializer.SerializeToElement(message));
                    return Task.CompletedTask;
                }, () => false, CancellationToken.None, desktopReady: () => true);
            await manager.HandleStartAsync(startJson.RootElement);
            Check(acks.Count == 1 && acks[0].GetProperty("stage").GetString() == "ready");
            await manager.HandleStartAsync(startJson.RootElement);
            Check(acks.Count == 2 && acks[1].GetProperty("stage").GetString() == "rejected");
            var chunk = CliCommandCrypto.Encrypt(key, DeviceId, ClientId,
                RequestId, "send_text", ThreadId, "browser", "chunk", 1, "",
                [0x41]); // Must be two bytes according to encrypted start.
            using var chunkJson = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "command_chunk", requestId = RequestId, clientId = ClientId,
                seq = 1, nonce = chunk.Nonce, ciphertext = chunk.Ciphertext
            }));
            await manager.HandleChunkAsync(chunkJson.RootElement);
            Check(acks.Count == 3 && acks[2].GetProperty("ackSeq").GetInt32() == 2
                && acks[2].GetProperty("stage").GetString() == "rejected");
            var rejection = CliCommandCrypto.Decrypt(key, DeviceId, ClientId,
                RequestId, "send_text", ThreadId, "device", "ack", 2, "rejected",
                acks[2].GetProperty("nonce").GetString()!,
                acks[2].GetProperty("ciphertext").GetString()!, 1024);
            try
            {
                using var result = JsonDocument.Parse(rejection);
                Check(result.RootElement.GetProperty("reason").GetString() == "invalid_payload"
                    && !result.RootElement.GetProperty("maybeEntered").GetBoolean());
            }
            finally { CryptographicOperations.ZeroMemory(rejection); }

            await using var lockedManager = new CliCommandManager(store, DeviceId,
                (message, _) =>
                {
                    acks.Add(JsonSerializer.SerializeToElement(message));
                    return Task.CompletedTask;
                }, () => true, CancellationToken.None, desktopReady: () => false);
            await lockedManager.HandleStartAsync(startJson.RootElement);
            Check(acks.Count == 4 && acks[3].GetProperty("stage").GetString() == "rejected");
            var locked = CliCommandCrypto.Decrypt(key, DeviceId, ClientId,
                RequestId, "send_text", ThreadId, "device", "ack", 1, "rejected",
                acks[3].GetProperty("nonce").GetString()!,
                acks[3].GetProperty("ciphertext").GetString()!, 1024);
            try
            {
                using var result = JsonDocument.Parse(locked);
                Check(result.RootElement.GetProperty("reason").GetString() == "locked");
            }
            finally { CryptographicOperations.ZeroMemory(locked); }

            var guardCalls = 0;
            var noInput = await ForegroundTerminalSender.SendAsync(
                new CapturedSession(new IntPtr(1), "", "", "", ThreadId), "hello",
                remoteInputGuard: () => { guardCalls++; return false; });
            Check(!noInput.Entered && guardCalls > 0);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("CLI command self-test failed.");
    }
}
