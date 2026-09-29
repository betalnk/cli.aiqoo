using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

internal static class CodexLoadedThreadResolverSelfTests
{
    private const string ThreadId = "123e4567-e89b-42d3-a456-426614174001";
    private const string SamePrefixId = "123e4567-e89b-42d3-a456-426619999999";
    private const string OtherId = "223e4567-e89b-42d3-a456-426614174002";
    private static readonly string DisplayedId = ThreadId[..29] + "...";

    internal static async Task RunAsync()
    {
        foreach (var invalid in new[] { "", ThreadId, ThreadId[..29] + "…", "123e4567-E89b-42d3-a456-42661..." })
            Check(!(await CodexLoadedThreadResolver.ResolveAsync(
                new("ws://127.0.0.1:1"), invalid)).Success);
        Check(!(await CodexLoadedThreadResolver.ResolveAsync(
            new("ws://localhost:1234"), DisplayedId)).Success);

        await HappyPathAsync();
        await NoMatchAsync();
        await AmbiguousAsync();
        await WrongMetadataAsync();
        await BearerRedactionAsync();
    }

    private static async Task HappyPathAsync()
    {
        await using var server = new MockServer(async (socket, auth) =>
        {
            Check(auth is null);
            await HandshakeAsync(socket);
            await LoadedAsync(socket, OtherId, ThreadId);
            await MetadataAsync(socket, ThreadId, "cli", @"C:\work\sample", "Новая задача");
        });
        var result = await CodexLoadedThreadResolver.ResolveAsync(new(server.Url), DisplayedId);
        Check(result.Identity is { ThreadId: ThreadId, Cwd: @"C:\work\sample", Name: "Новая задача" });
        await server.Completion;
    }

    private static async Task NoMatchAsync()
    {
        await using var server = new MockServer(async (socket, _) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, OtherId);
        });
        Check(!(await CodexLoadedThreadResolver.ResolveAsync(new(server.Url), DisplayedId)).Success);
        await server.Completion;
    }

    private static async Task AmbiguousAsync()
    {
        await using var server = new MockServer(async (socket, _) =>
        {
            await HandshakeAsync(socket);
            await LoadedAsync(socket, ThreadId, SamePrefixId);
        });
        Check(!(await CodexLoadedThreadResolver.ResolveAsync(new(server.Url), DisplayedId)).Success);
        await server.Completion;
    }

    private static async Task WrongMetadataAsync()
    {
        foreach (var (id, source, cwd) in new[]
        {
            (OtherId, "cli", @"C:\work"),
            (ThreadId, "api", @"C:\work"),
            (ThreadId, "cli", ""),
            (ThreadId, "cli", "relative")
        })
        {
            await using var server = new MockServer(async (socket, _) =>
            {
                await HandshakeAsync(socket);
                await LoadedAsync(socket, ThreadId);
                await MetadataAsync(socket, id, source, cwd, null);
            });
            Check(!(await CodexLoadedThreadResolver.ResolveAsync(new(server.Url), DisplayedId)).Success);
            await server.Completion;
        }
    }

    private static async Task BearerRedactionAsync()
    {
        const string secret = "resolver-secret-must-not-appear";
        var tokenPath = Path.Combine(Path.GetTempPath(),
            "codex-voice-resolver-token-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(tokenPath, secret + "\n", new UTF8Encoding(false));
            await using var server = new MockServer(async (socket, auth) =>
            {
                Check(auth == "Bearer " + secret);
                await HandshakeAsync(socket);
                await LoadedAsync(socket, ThreadId);
                var read = await ReceiveAsync(socket);
                Check(Method(read) == "thread/read" && Id(read) == 3);
                await ReplyAsync(socket, new { id = 3, error = new { message = secret } });
            });
            var result = await CodexLoadedThreadResolver.ResolveAsync(
                new(server.Url, tokenPath), DisplayedId);
            Check(!result.Success && !result.Error.Contains(secret, StringComparison.Ordinal));
            await server.Completion;
        }
        finally
        {
            if (File.Exists(tokenPath)) File.Delete(tokenPath);
        }
    }

    private static async Task HandshakeAsync(WebSocket socket)
    {
        var hello = await ReceiveAsync(socket);
        Check(Method(hello) == "initialize" && Id(hello) == 1);
        await ReplyAsync(socket, new { id = 1, result = new { userAgent = "mock" } });
        Check(Method(await ReceiveAsync(socket)) == "initialized");
    }

    private static async Task LoadedAsync(WebSocket socket, params string[] ids)
    {
        var loaded = await ReceiveAsync(socket);
        Check(Method(loaded) == "thread/loaded/list" && Id(loaded) == 2);
        await ReplyAsync(socket, new { id = 2, result = new { data = ids } });
    }

    private static async Task MetadataAsync(
        WebSocket socket, string responseId, string source, string cwd, string? name)
    {
        var read = await ReceiveAsync(socket);
        Check(Method(read) == "thread/read" && Id(read) == 3);
        Check(read.GetProperty("params").GetProperty("threadId").GetString() == ThreadId);
        Check(!read.GetProperty("params").GetProperty("includeTurns").GetBoolean());
        await ReplyAsync(socket, new { id = 3, result = new
        {
            thread = new { id = responseId, source, cwd, name }
        } });
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        using var payload = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            Check(frame.MessageType == WebSocketMessageType.Text);
            payload.Write(buffer, 0, frame.Count);
            Check(payload.Length < 64 * 1024);
            if (frame.EndOfMessage)
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
        if (!condition) throw new InvalidOperationException("Codex loaded-thread resolver self-test failed.");
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
            var headers = await ReadHttpHeaderAsync(stream);
            var key = Header(headers, "Sec-WebSocket-Key")
                ?? throw new InvalidDataException("Missing WebSocket key.");
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var reply = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            await stream.WriteAsync(reply);
            using var socket = WebSocket.CreateFromStream(stream, isServer: true,
                subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            await script(socket, Header(headers, "Authorization"));
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
            catch (Exception) { /* The explicit test assertion reports the original failure. */ }
        }
    }
}
