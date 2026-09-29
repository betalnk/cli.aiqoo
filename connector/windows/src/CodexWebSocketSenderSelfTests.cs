using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

/// <summary>Loopback mock app-server; no Codex process or user thread is contacted.</summary>
internal static class CodexWebSocketSenderSelfTests
{
    private const string ThreadId = "123e4567-e89b-42d3-a456-426614174000";
    private const string OtherThreadId = "123e4567-e89b-42d3-a456-426614174001";
    private const string TurnId = "123e4567-e89b-42d3-a456-426614174002";
    private const string OtherTurnId = "123e4567-e89b-42d3-a456-426614174003";

    internal static async Task RunAsync()
    {
        var previousUrl = Environment.GetEnvironmentVariable("CODEX_VOICE_APP_SERVER_URL");
        var previousTokenFile = Environment.GetEnvironmentVariable("CODEX_VOICE_APP_SERVER_TOKEN_FILE");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_VOICE_APP_SERVER_URL", null);
            Environment.SetEnvironmentVariable("CODEX_VOICE_APP_SERVER_TOKEN_FILE", null);
            Check(CodexWebSocketConfig.FromEnvironment() is null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_VOICE_APP_SERVER_URL", previousUrl);
            Environment.SetEnvironmentVariable("CODEX_VOICE_APP_SERVER_TOKEN_FILE", previousTokenFile);
        }

        foreach (var badUrl in new[] {
            "", "ws://localhost:1234", "ws://0.0.0.0:1234", "ws://127.0.0.2:1234",
            "ws://127.0.0.1", "ws://127.0.0.1:0", "ws://127.0.0.1:65536",
            "ws://127.0.0.1:1234/path", "ws://user@127.0.0.1:1234",
            "ws://127.0.0.1:1234/?x=y", "http://127.0.0.1:1234"
        })
            Check(!CodexWebSocketSender.TryGetEndpoint(new(badUrl), out _));
        Check(CodexWebSocketSender.TryGetEndpoint(new("ws://127.0.0.1:1234"), out _));
        Check(CodexWebSocketSender.TryGetEndpoint(new("wss://127.0.0.1:1234/"), out _));
        Check((await CodexWebSocketSender.SendAsync(
            new("ws://127.0.0.1:1"), "invalid-uuid", "do not send")).Status == CodexSendStatus.Rejected);

        await ProbeAndIdleAsync();
        await WrongTargetAndSourceAsync();
        await ActiveTurnAsync();
        await DispatchTimeoutAsync();
        await BearerRedactionAsync();
    }

    private static async Task ProbeAndIdleAsync()
    {
        await using (var server = new MockServer(async (socket, auth) =>
        {
            Check(auth is null);
            var hello = await ReceiveAsync(socket);
            Check(Method(hello) == "initialize" && Id(hello) == 1);
            await ReplyAsync(socket, new { id = 1, result = new { userAgent = "mock" } });
            Check(Method(await ReceiveAsync(socket)) == "initialized");
        }))
        {
            var result = await CodexWebSocketSender.ProbeConnectionAsync(new(server.Url));
            Check(result.Status == CodexConnectionStatus.Connected);
            await server.Completion;
        }

        await using var idleServer = new MockServer(async (socket, auth) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, ThreadId);
            await MetadataAsync(socket, ThreadId, "cli", "idle");
            var start = await ReceiveAsync(socket);
            Check(Method(start) == "turn/start" && Id(start) == 7);
            Check(start.GetProperty("params").GetProperty("threadId").GetString() == ThreadId);
            Check(start.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()
                == CodexQueueSender.ConnectorPrefix + "\nПривет && `whoami`");
            await ReplyAsync(socket, new { id = 7, result = new { threadId = ThreadId, turn = new { id = TurnId } } });
        });
        var accepted = await CodexWebSocketSender.SendAsync(
            new(idleServer.Url), ThreadId, "Привет && `whoami`");
        Check(accepted.Status == CodexSendStatus.Accepted && accepted.TurnId == TurnId);
        await idleServer.Completion;
    }

    private static async Task WrongTargetAndSourceAsync()
    {
        await using (var unloaded = new MockServer(async (socket, auth) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, OtherThreadId);
        }))
        {
            var result = await CodexWebSocketSender.SendAsync(new(unloaded.Url), ThreadId, "never dispatch");
            Check(result.Status == CodexSendStatus.Rejected);
            await unloaded.Completion;
        }

        await using (var wrongSource = new MockServer(async (socket, auth) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, ThreadId);
            await MetadataAsync(socket, ThreadId, "api", "idle");
        }))
        {
            var result = await CodexWebSocketSender.SendAsync(new(wrongSource.Url), ThreadId, "never dispatch");
            Check(result.Status == CodexSendStatus.Rejected);
            await wrongSource.Completion;
        }

        await using (var wrongRead = new MockServer(async (socket, auth) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, ThreadId);
            await MetadataAsync(socket, OtherThreadId, "cli", "idle");
        }))
        {
            var result = await CodexWebSocketSender.SendAsync(new(wrongRead.Url), ThreadId, "never dispatch");
            Check(result.Status == CodexSendStatus.Rejected);
            await wrongRead.Completion;
        }
    }

    private static async Task ActiveTurnAsync()
    {
        await using (var active = new MockServer(async (socket, auth) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, ThreadId);
            await MetadataAsync(socket, ThreadId, "cli", "active");
            var list = await ReceiveAsync(socket);
            Check(Method(list) == "thread/turns/list" && Id(list) == 5);
            await ReplyAsync(socket, new {
                id = 5, result = new { data = new[] { new { id = TurnId, status = "inProgress" } } }
            });
            var steer = await ReceiveAsync(socket);
            Check(Method(steer) == "turn/steer" && Id(steer) == 7);
            Check(steer.GetProperty("params").GetProperty("expectedTurnId").GetString() == TurnId);
            await ReplyAsync(socket, new { id = 7, result = new { turnId = TurnId } });
        }))
        {
            var result = await CodexWebSocketSender.SendAsync(new(active.Url), ThreadId, "steer");
            Check(result.Status == CodexSendStatus.Accepted && result.TurnId == TurnId);
            await active.Completion;
        }

        await using (var mismatch = new MockServer(async (socket, auth) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, ThreadId);
            await MetadataAsync(socket, ThreadId, "cli", "active");
            Check(Method(await ReceiveAsync(socket)) == "thread/turns/list");
            await ReplyAsync(socket, new {
                id = 5, result = new { data = new[] { new { id = TurnId, status = "inProgress" } } }
            });
            Check(Method(await ReceiveAsync(socket)) == "turn/steer");
            await ReplyAsync(socket, new { id = 7, result = new { turnId = OtherTurnId } });
        }))
        {
            var result = await CodexWebSocketSender.SendAsync(new(mismatch.Url), ThreadId, "steer");
            Check(result.Status == CodexSendStatus.Unknown && result.TurnId is null);
            await mismatch.Completion;
        }
    }

    private static async Task DispatchTimeoutAsync()
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new MockServer(async (socket, auth) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, ThreadId);
            await MetadataAsync(socket, ThreadId, "cli", "idle");
            Check(Method(await ReceiveAsync(socket)) == "turn/start");
            dispatched.SetResult();
            await release.Task;
        });
        using var cancel = new CancellationTokenSource();
        var sending = CodexWebSocketSender.SendAsync(new(server.Url), ThreadId, "one attempt", cancel.Token);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.CancelAfter(TimeSpan.FromMilliseconds(100));
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Status == CodexSendStatus.Unknown);
        release.SetResult();
        await server.Completion;
    }

    private static async Task BearerRedactionAsync()
    {
        const string secret = "test-secret-never-log-123";
        var path = Path.Combine(Path.GetTempPath(), "codex-voice-ws-token-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, secret + "\n", new UTF8Encoding(false));
            await using var server = new MockServer(async (socket, auth) =>
            {
                Check(auth == "Bearer " + secret);
                await HandshakeAsync(socket);
                await LoadedAsync(socket, ThreadId);
                await MetadataAsync(socket, ThreadId, "cli", "idle");
                Check(Method(await ReceiveAsync(socket)) == "turn/start");
                await ReplyAsync(socket, new { id = 7, error = new { code = -32000, message = secret } });
            });
            var result = await CodexWebSocketSender.SendAsync(new(server.Url, path), ThreadId, "secret check");
            Check(result.Status == CodexSendStatus.Rejected && !result.Message.Contains(secret, StringComparison.Ordinal));
            await server.Completion;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task HandshakeAsync(WebSocket socket)
    {
        var hello = await ReceiveAsync(socket);
        Check(Method(hello) == "initialize" && Id(hello) == 1);
        await ReplyAsync(socket, new { id = 1, result = new { userAgent = "mock" } });
        Check(Method(await ReceiveAsync(socket)) == "initialized");
    }

    private static async Task LoadedAsync(WebSocket socket, string loadedId)
    {
        var loaded = await ReceiveAsync(socket);
        Check(Method(loaded) == "thread/loaded/list" && Id(loaded) == 2);
        await ReplyAsync(socket, new { id = 2, result = new { data = new[] { loadedId } } });
    }

    private static async Task MetadataAsync(WebSocket socket, string responseId, string source, string status)
    {
        var read = await ReceiveAsync(socket);
        Check(Method(read) == "thread/read" && Id(read) == 4);
        Check(read.GetProperty("params").GetProperty("threadId").GetString() == ThreadId);
        await ReplyAsync(socket, new { id = 4, result = new {
            thread = new { id = responseId, source, status = new { type = status } }
        } });
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        using var payload = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            Check(result.MessageType == WebSocketMessageType.Text);
            payload.Write(buffer, 0, result.Count);
            Check(payload.Length < 64 * 1024);
            if (result.EndOfMessage)
            {
                using var json = JsonDocument.Parse(payload.ToArray());
                return json.RootElement.Clone();
            }
        }
    }

    private static Task ReplyAsync(WebSocket socket, object response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        return socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).AsTask();
    }

    private static string? Method(JsonElement request) => request.GetProperty("method").GetString();
    private static int Id(JsonElement request) => request.GetProperty("id").GetInt32();

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Codex WebSocket sender self-test failed.");
    }

    private sealed class MockServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _completion;

        internal string Url { get; }
        internal Task Completion => _completion.WaitAsync(TimeSpan.FromSeconds(5));

        internal MockServer(Func<WebSocket, string?, Task> script)
        {
            _listener.Start();
            Url = "ws://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _completion = ServeAsync(script);
        }

        private async Task ServeAsync(Func<WebSocket, string?, Task> script)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var header = await ReadHttpHeaderAsync(stream);
            var key = Header(header, "Sec-WebSocket-Key")
                ?? throw new InvalidDataException("Missing WebSocket key.");
            var auth = Header(header, "Authorization");
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var reply = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            await stream.WriteAsync(reply);
            using var socket = WebSocket.CreateFromStream(stream, isServer: true,
                subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            await script(socket, auth);
        }

        private static async Task<string> ReadHttpHeaderAsync(Stream stream)
        {
            var bytes = new List<byte>();
            var last = new byte[4];
            var one = new byte[1];
            while (bytes.Count < 16 * 1024)
            {
                if (await stream.ReadAsync(one) != 1) throw new EndOfStreamException();
                bytes.Add(one[0]);
                last[0] = last[1]; last[1] = last[2]; last[2] = last[3]; last[3] = one[0];
                if (last.SequenceEqual(new byte[] { 13, 10, 13, 10 }))
                    return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new InvalidDataException("WebSocket handshake is too large.");
        }

        private static string? Header(string headers, string name)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line[(colon + 1)..].Trim();
            }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* The awaited test assertion reports the original failure. */ }
        }
    }
}
