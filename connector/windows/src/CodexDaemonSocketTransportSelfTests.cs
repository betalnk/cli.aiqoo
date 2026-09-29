using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace CodexVoice;

internal static class CodexDaemonSocketTransportSelfTests
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    internal static async Task RunAsync()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            throw new PlatformNotSupportedException("AF_UNIX is unavailable on this host.");
        await ExchangeWithLocalWebSocketAsync();
        await RejectInvalidAcceptAsync();
    }

    private static async Task ExchangeWithLocalWebSocketAsync()
    {
        var path = NewTestSocketPath();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var serverTask = ServeOneExchangeAsync(listener, timeout.Token);
            using var client = await CodexDaemonSocketTransport.ConnectAsync(path, timeout.Token);
            var request = Encoding.UTF8.GetBytes("local-test-frame");
            await client.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text,
                endOfMessage: true, timeout.Token);
            var replyBuffer = new byte[32];
            var reply = await client.ReceiveAsync(new ArraySegment<byte>(replyBuffer), timeout.Token);
            Check(reply.MessageType == WebSocketMessageType.Text && reply.EndOfMessage
                && Encoding.UTF8.GetString(replyBuffer, 0, reply.Count) == "local-test-reply");
            await serverTask;
        }
        finally
        {
            listener.Dispose();
            File.Delete(path);
        }
    }

    private static async Task ServeOneExchangeAsync(Socket listener, CancellationToken token)
    {
        using var accepted = await listener.AcceptAsync(token);
        using var stream = new NetworkStream(accepted, ownsSocket: false);
        var request = await ReadRequestHeaderAsync(stream, token);
        Check(request.StartsWith("GET /rpc HTTP/1.1\r\n", StringComparison.Ordinal));
        Check(!request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
        Check(!request.Contains("Cookie:", StringComparison.OrdinalIgnoreCase));
        Check(!request.Contains("Origin:", StringComparison.OrdinalIgnoreCase));
        await WriteResponseAsync(stream, GetClientKey(request), validAccept: true, token);

        using var server = WebSocket.CreateFromStream(stream, isServer: true,
            subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        var input = new byte[32];
        var received = await server.ReceiveAsync(new ArraySegment<byte>(input), token);
        Check(received.MessageType == WebSocketMessageType.Text && received.EndOfMessage
            && Encoding.UTF8.GetString(input, 0, received.Count) == "local-test-frame");
        var reply = Encoding.UTF8.GetBytes("local-test-reply");
        await server.SendAsync(new ArraySegment<byte>(reply), WebSocketMessageType.Text,
            endOfMessage: true, token);
    }

    private static async Task RejectInvalidAcceptAsync()
    {
        var path = NewTestSocketPath();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var serverTask = ServeInvalidAcceptAsync(listener, timeout.Token);
            try
            {
                using var unexpected = await CodexDaemonSocketTransport.ConnectAsync(path, timeout.Token);
                throw new InvalidOperationException("An invalid WebSocket accept header was accepted.");
            }
            catch (InvalidDataException)
            {
                // The transport must reject a server that cannot prove the handshake nonce.
            }
            await serverTask;
        }
        finally
        {
            listener.Dispose();
            File.Delete(path);
        }
    }

    private static async Task ServeInvalidAcceptAsync(Socket listener, CancellationToken token)
    {
        using var accepted = await listener.AcceptAsync(token);
        using var stream = new NetworkStream(accepted, ownsSocket: false);
        var request = await ReadRequestHeaderAsync(stream, token);
        await WriteResponseAsync(stream, GetClientKey(request), validAccept: false, token);
    }

    private static string NewTestSocketPath() =>
        Path.Combine(Path.GetTempPath(), $"cvws-{Guid.NewGuid():N}.sock");

    private static async Task<string> ReadRequestHeaderAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[1024];
        var oneByte = new byte[1];
        var length = 0;
        while (length < bytes.Length)
        {
            Check(await stream.ReadAsync(oneByte.AsMemory(), token) == 1);
            bytes[length++] = oneByte[0];
            if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n'
                && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                return Encoding.ASCII.GetString(bytes, 0, length);
        }
        throw new InvalidDataException("The test client request header is too large.");
    }

    private static string GetClientKey(string request)
    {
        var keyLine = request.Split("\r\n", StringSplitOptions.None)
            .Single(line => line.StartsWith("Sec-WebSocket-Key: ", StringComparison.OrdinalIgnoreCase));
        return keyLine["Sec-WebSocket-Key: ".Length..];
    }

    private static Task WriteResponseAsync(Stream stream, string key, bool validAccept, CancellationToken token)
    {
        var accept = validAccept
            ? Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)))
            : Convert.ToBase64String(new byte[20]);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        return stream.WriteAsync(response.AsMemory(), token).AsTask();
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Codex daemon socket transport self-test failed.");
    }
}
