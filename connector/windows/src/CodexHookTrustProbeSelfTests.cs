using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexVoice;

/// <summary>Isolated fake-server checks; never contacts a Codex process.</summary>
internal static class CodexHookTrustProbeSelfTests
{
    private const string Secret = "hook-probe-bearer-must-stay-private";
    private const string Command = "CodexVoice.exe --register-from-hook";

    internal static async Task RunAsync()
    {
        var home = Path.Combine(Path.GetTempPath(),
            "codex-hook-trust-tests-" + Guid.NewGuid().ToString("N"));
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var pluginRoot = Path.Combine(home, "plugins", "cache", "cli", "codex-voice", "0.1.0");
            var hooksPath = Path.Combine(pluginRoot, "hooks", "hooks.json");
            var manifestPath = Path.Combine(pluginRoot, ".codex-plugin", "plugin.json");
            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(hooksPath, "{}", new UTF8Encoding(false));
            await File.WriteAllTextAsync(manifestPath,
                "{\"name\":\"codex-voice\",\"version\":\"0.1.0\"}", new UTF8Encoding(false));
            var tokenPath = Path.Combine(home, "token.txt");
            await File.WriteAllTextAsync(tokenPath, Secret + "\n", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("CODEX_HOME", home);

            var cwd = Path.Combine(home, "project");
            Directory.CreateDirectory(cwd);
            await HappyPathAsync(cwd, hooksPath, tokenPath);
            await ErrorRedactionAsync(cwd, tokenPath);
            ParserCases(cwd, hooksPath);

            // Two plausible cached versions cannot establish which definition is active.
            Directory.CreateDirectory(Path.Combine(home, "plugins", "cache", "cli",
                "codex-voice", "0.2.0"));
            var unavailable = await CodexHookTrustProbe.ProbeAsync(
                new("ws://127.0.0.1:1"), cwd);
            Check(unavailable.Status == CodexHookTrustStatus.NotTrusted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            var fullHome = Path.GetFullPath(home);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (fullHome.StartsWith(tempRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullHome).StartsWith("codex-hook-trust-tests-",
                    StringComparison.Ordinal)
                && Directory.Exists(fullHome))
                Directory.Delete(fullHome, recursive: true);
        }
    }

    private static async Task HappyPathAsync(string cwd, string hooksPath, string tokenPath)
    {
        await using var server = new MockServer(async (socket, auth) =>
        {
            Check(auth == "Bearer " + Secret);
            await HandshakeAsync(socket);
            var request = await ReceiveAsync(socket);
            Check(request.GetProperty("method").GetString() == "hooks/list");
            Check(request.GetProperty("id").GetInt32() == 2);
            var requested = request.GetProperty("params").GetProperty("cwds");
            Check(requested.GetArrayLength() == 1 && requested[0].GetString() == cwd);
            await ReplyAsync(socket, GoodResponse(cwd, hooksPath));
        });
        var result = await CodexHookTrustProbe.ProbeAsync(
            new(server.Url, tokenPath), cwd);
        Check(result.Trusted && result.Status == CodexHookTrustStatus.Trusted);
        await server.Completion;
    }

    private static async Task ErrorRedactionAsync(string cwd, string tokenPath)
    {
        await using var server = new MockServer(async (socket, auth) =>
        {
            Check(auth == "Bearer " + Secret);
            await HandshakeAsync(socket);
            Check((await ReceiveAsync(socket)).GetProperty("method").GetString() == "hooks/list");
            await ReplyAsync(socket, new { id = 2, error = new { message = Secret } });
        });
        var result = await CodexHookTrustProbe.ProbeAsync(
            new(server.Url, tokenPath), cwd);
        Check(!result.Trusted && !result.Message.Contains(Secret, StringComparison.Ordinal));
        await server.Completion;
    }

    private static void ParserCases(string cwd, string hooksPath)
    {
        Check(Evaluate(GoodResponse(cwd, hooksPath), cwd, hooksPath).Trusted);

        // pluginId is optional in the protocol. The exact installed path remains required.
        var optionalId = GoodResponse(cwd, hooksPath);
        Hooks(optionalId)[0]!["pluginId"] = null;
        Hooks(optionalId)[1]!["pluginId"] = null;
        Check(Evaluate(optionalId, cwd, hooksPath).Trusted);

        Reject(root => Hooks(root)[0]!["sourcePath"] = hooksPath + "-evil", cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["sourcePath"] = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(hooksPath)!)!,
            "0.1.0-evil", "hooks", "hooks.json"), cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["sourcePath"] = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(hooksPath)!)!,
            "0.2.0", "hooks", "hooks.json"), cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["pluginId"] = "codex-voice@other", cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["source"] = "user", cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["handlerType"] = "prompt", cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["command"] = Command + " --extra", cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["enabled"] = false, cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["trustStatus"] = "untrusted", cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["async"] = true, cwd, hooksPath);
        Reject(root => Hooks(root)[0]!["eventName"] = "other", cwd, hooksPath);
        Reject(root => Hooks(root).Add(Hooks(root)[0]!.DeepClone()), cwd, hooksPath);
        Reject(root => Hooks(root).RemoveAt(0), cwd, hooksPath);
        Reject(root => root["result"]!["data"]![0]!["warnings"]!.AsArray().Add("incomplete"),
            cwd, hooksPath);
        Reject(root => root["result"]!["data"]![0]!["errors"]!.AsArray().Add(
            new JsonObject { ["path"] = hooksPath, ["message"] = Secret }), cwd, hooksPath);
        Reject(root => root["result"]!["data"]![0]!["cwd"] = cwd + "-other", cwd, hooksPath);
        Reject(root => root["result"]!["data"]!.AsArray().Add(
            root["result"]!["data"]![0]!.DeepClone()), cwd, hooksPath);
        Reject(root => Hooks(root).Add(new JsonObject
        {
            ["key"] = "user:session_start:0:0", ["eventName"] = "sessionStart",
            ["handlerType"] = "command", ["command"] = Command,
            ["sourcePath"] = Path.Combine(Path.GetTempPath(), "user-hooks.json"),
            ["source"] = "user", ["pluginId"] = null,
            ["enabled"] = true, ["trustStatus"] = "trusted"
        }), cwd, hooksPath);
    }

    private static void Reject(Action<JsonObject> change, string cwd, string hooksPath)
    {
        var root = GoodResponse(cwd, hooksPath);
        change(root);
        Check(!Evaluate(root, cwd, hooksPath).Trusted);
    }

    private static CodexHookTrustResult Evaluate(JsonObject response, string cwd, string hooksPath)
    {
        using var document = JsonDocument.Parse(response.ToJsonString());
        return CodexHookTrustProbe.EvaluateResponse(document.RootElement, cwd, hooksPath);
    }

    private static JsonArray Hooks(JsonObject root) =>
        root["result"]!["data"]![0]!["hooks"]!.AsArray();

    private static JsonObject GoodResponse(string cwd, string hooksPath) =>
        JsonSerializer.SerializeToNode(new
        {
            id = 2,
            result = new
            {
                data = new[]
                {
                    new
                    {
                        cwd,
                        errors = Array.Empty<object>(),
                        warnings = Array.Empty<string>(),
                        hooks = new[]
                        {
                            new
                            {
                                key = "codex-voice@cli:hooks/hooks.json:session_start:0:0",
                                eventName = "sessionStart", handlerType = "command",
                                command = Command, sourcePath = hooksPath, source = "plugin",
                                pluginId = "codex-voice@cli", enabled = true,
                                trustStatus = "trusted", @async = false
                            },
                            new
                            {
                                key = "codex-voice@cli:hooks/hooks.json:user_prompt_submit:0:0",
                                eventName = "userPromptSubmit", handlerType = "command",
                                command = Command, sourcePath = hooksPath, source = "plugin",
                                pluginId = "codex-voice@cli", enabled = true,
                                trustStatus = "trusted", @async = false
                            }
                        }
                    }
                }
            }
        })!.AsObject();

    private static async Task HandshakeAsync(WebSocket socket)
    {
        var hello = await ReceiveAsync(socket);
        Check(hello.GetProperty("method").GetString() == "initialize");
        Check(hello.GetProperty("id").GetInt32() == 1);
        await ReplyAsync(socket, new { id = 1, result = new { userAgent = "mock" } });
        Check((await ReceiveAsync(socket)).GetProperty("method").GetString() == "initialized");
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var bytes = new byte[8192];
        using var payload = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(bytes.AsMemory(), CancellationToken.None);
            Check(frame.MessageType == WebSocketMessageType.Text);
            payload.Write(bytes, 0, frame.Count);
            Check(payload.Length < 64 * 1024);
            if (frame.EndOfMessage)
            {
                using var document = JsonDocument.Parse(payload.ToArray());
                return document.RootElement.Clone();
            }
        }
    }

    private static Task ReplyAsync(WebSocket socket, object response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        return socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true,
            CancellationToken.None).AsTask();
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Codex hook trust probe self-test failed.");
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
