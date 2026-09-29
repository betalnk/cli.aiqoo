using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace CodexVoice;

/// <summary>
/// Opens the local Codex app-server control socket as a WebSocket. This only
/// connects to an existing daemon; it never starts one or submits a prompt.
/// </summary>
internal static class CodexDaemonSocketTransport
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxResponseHeaderBytes = 8192;
    private const int MaxResponseHeaderLines = 32;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    internal static string GetDefaultSocketPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (codexHome is null)
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userHome))
                throw new InvalidOperationException("The user profile directory is unavailable.");
            codexHome = Path.Combine(userHome, ".codex");
        }

        ValidateLocalPath(codexHome);
        return Path.Combine(codexHome, "app-server-control", "app-server-control.sock");
    }

    internal static Task<WebSocket> ConnectAsync(CancellationToken token = default) =>
        ConnectAsync(GetDefaultSocketPath(), token);

    // Explicit path is used by the isolated AF_UNIX interoperability test.
    internal static async Task<WebSocket> ConnectAsync(string socketPath, CancellationToken token = default)
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            throw new PlatformNotSupportedException("This Windows host does not support Unix domain sockets.");
        ValidateLocalPath(socketPath);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(HandshakeTimeout);
        Socket? connection = null;
        NetworkStream? stream = null;
        try
        {
            connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await connection.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
            stream = new NetworkStream(connection, ownsSocket: true);
            connection = null;

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = Encoding.ASCII.GetBytes(
                "GET /rpc HTTP/1.1\r\n" +
                "Host: localhost\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n");
            await stream.WriteAsync(request.AsMemory(), timeout.Token);
            var response = await ReadResponseHeaderAsync(stream, timeout.Token);
            ValidateUpgradeResponse(response, key);

            var webSocket = WebSocket.CreateFromStream(
                stream, isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            stream = null; // The WebSocket now owns the stream and socket.
            return webSocket;
        }
        finally
        {
            stream?.Dispose();
            connection?.Dispose();
        }
    }

    private static void ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Any(char.IsControl))
            throw new ArgumentException("A fully qualified local socket path is required.", nameof(path));
    }

    private static async Task<string> ReadResponseHeaderAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[MaxResponseHeaderBytes];
        var oneByte = new byte[1];
        var length = 0;
        while (length < bytes.Length)
        {
            if (await stream.ReadAsync(oneByte.AsMemory(), token) != 1)
                throw new IOException("The socket closed during the WebSocket handshake.");
            var current = oneByte[0];
            if (current > 0x7f || current == 0 || (current < 0x20 && current is not (byte)'\r' and not (byte)'\n' and not (byte)'\t'))
                throw new InvalidDataException("The WebSocket response contains invalid header bytes.");
            bytes[length++] = current;
            if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n'
                && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                return Encoding.ASCII.GetString(bytes, 0, length);
        }
        throw new InvalidDataException("The WebSocket response header is too large.");
    }

    private static void ValidateUpgradeResponse(string response, string key)
    {
        var lines = response.Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 5 || lines.Length > MaxResponseHeaderLines + 3
            || !(lines[0] == "HTTP/1.1 101"
                || lines[0].StartsWith("HTTP/1.1 101 ", StringComparison.Ordinal))
            || lines[0].IndexOfAny(['\r', '\n']) >= 0
            || lines[^1].Length != 0 || lines[^2].Length != 0)
            throw new InvalidDataException("The server did not return a valid HTTP 101 response.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length - 2; index++)
        {
            var line = lines[index];
            var colon = line.IndexOf(':');
            if (colon < 1 || line[0] is ' ' or '\t'
                || line.IndexOfAny(['\r', '\n']) >= 0
                || line.AsSpan(0, Math.Max(colon, 0)).IndexOfAny(' ', '\t') >= 0)
                throw new InvalidDataException("The WebSocket response has a malformed header.");
            var name = line[..colon];
            if (name.Any(character => character is < '!' or > '~' || character == ':')
                || !headers.TryAdd(name, line[(colon + 1)..].Trim(' ', '\t')))
                throw new InvalidDataException("The WebSocket response has a duplicate or invalid header.");
        }

        var expectedAccept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        if (!headers.TryGetValue("Upgrade", out var upgrade)
            || !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Connection", out var connection)
            || !connection.Split(',').Any(value => string.Equals(value.Trim(), "Upgrade", StringComparison.OrdinalIgnoreCase))
            || !headers.TryGetValue("Sec-WebSocket-Accept", out var accept)
            || !string.Equals(accept, expectedAccept, StringComparison.Ordinal)
            || headers.ContainsKey("Sec-WebSocket-Extensions")
            || headers.ContainsKey("Sec-WebSocket-Protocol"))
            throw new InvalidDataException("The server did not complete the requested WebSocket upgrade.");
    }
}
