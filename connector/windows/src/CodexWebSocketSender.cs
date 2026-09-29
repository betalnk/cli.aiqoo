using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexVoice;

/// <summary>A connection to an explicitly configured, local Codex app-server.</summary>
internal sealed record CodexWebSocketConfig(string Url, string? TokenFilePath = null)
{
    internal static CodexWebSocketConfig? FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("CODEX_VOICE_APP_SERVER_URL");
        return string.IsNullOrWhiteSpace(url) ? null : new(
            url.Trim(), Environment.GetEnvironmentVariable("CODEX_VOICE_APP_SERVER_TOKEN_FILE"));
    }
}

/// <summary>
/// Delivers directly to a loaded Codex CLI thread over the app-server WebSocket.
/// It never starts a server, resumes an unloaded thread, or falls back to a queue.
/// </summary>
internal static class CodexWebSocketSender
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private const int MaxProtocolMessages = 4096;
    private const int MaxTokenBytes = 4096;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex LoopbackUrl = new(
        @"\Awss?://127\.0\.0\.1:([0-9]{1,5})/?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool TryGetEndpoint(CodexWebSocketConfig config, out Uri? endpoint)
    {
        endpoint = null;
        if (config is null || config.Url is null) return false;
        var match = LoopbackUrl.Match(config.Url);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var port)
            || port is < 1 or > 65535
            || !Uri.TryCreate(config.Url, UriKind.Absolute, out var parsed)
            || parsed.Port != port || parsed.AbsolutePath != "/"
            || parsed.UserInfo.Length != 0 || parsed.Query.Length != 0 || parsed.Fragment.Length != 0)
            return false;
        endpoint = parsed;
        return true;
    }

    /// <summary>Checks the handshake only. It does not list or resume threads.</summary>
    internal static async Task<CodexConnectionResult> ProbeConnectionAsync(
        CodexWebSocketConfig config, CancellationToken token = default)
    {
        if (!TryGetEndpoint(config, out var endpoint))
            return new(CodexConnectionStatus.Unavailable, "Укажите локальный WebSocket Codex: ws://127.0.0.1:порт.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var socket = await ConnectAsync(endpoint!, config.TokenFilePath, timeout.Token);
            return await ProbeOverProtocolAsync(socket, timeout.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Neither server responses nor exception text are safe to expose: they may echo the bearer.
            return new(CodexConnectionStatus.Unavailable, "Нет прямого соединения с локальным Codex app-server.");
        }
    }

    internal static async Task<CodexConnectionResult> ProbeOverProtocolAsync(
        WebSocket socket, CancellationToken token = default)
    {
        using var hello = await RequestAsync(socket, 1, InitializeRequest(), token);
        RequireResult(hello.RootElement);
        await WriteAsync(socket, new { method = "initialized", @params = new { } }, token);
        return new(CodexConnectionStatus.Connected, "Прямое соединение с Codex доступно.");
    }

    internal static async Task<CodexSendResult> SendAsync(
        CodexWebSocketConfig config, string threadId, string message, CancellationToken token = default)
    {
        if (!TryGetEndpoint(config, out var endpoint))
            return new(CodexSendStatus.Rejected, "Укажите локальный WebSocket Codex: ws://127.0.0.1:порт.");
        if (!Guid.TryParseExact(threadId, "D", out var parsed)
            || parsed.ToString("D") != threadId || string.IsNullOrWhiteSpace(message))
            return new(CodexSendStatus.Rejected, "Неверный UUID сессии или пустое сообщение.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SendTimeout);
        try
        {
            using var socket = await ConnectAsync(endpoint!, config.TokenFilePath, timeout.Token);
            return await SendOverProtocolAsync(socket, threadId, message, timeout.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(CodexSendStatus.Rejected,
                "Прямое соединение с локальным Codex не установлено. Сообщение не отправлено.");
        }
    }

    /// <summary>Protocol core, exercised against an in-process WebSocket server in self-tests.</summary>
    internal static async Task<CodexSendResult> SendOverProtocolAsync(
        WebSocket socket, string threadId, string message, CancellationToken token = default)
    {
        if (!Guid.TryParseExact(threadId, "D", out var parsed)
            || parsed.ToString("D") != threadId || string.IsNullOrWhiteSpace(message))
            return new(CodexSendStatus.Rejected, "Неверный UUID сессии или пустое сообщение.");

        var dispatchMayHaveReachedServer = false;
        try
        {
            using (var hello = await RequestAsync(socket, 1, InitializeRequest(), token))
                RequireResult(hello.RootElement);
            await WriteAsync(socket, new { method = "initialized", @params = new { } }, token);

            using (var loaded = await RequestAsync(socket, 2,
                new { method = "thread/loaded/list", id = 2, @params = new { } }, token))
            {
                var data = RequireResult(loaded.RootElement).GetProperty("data");
                if (data.ValueKind != JsonValueKind.Array || !data.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.String && item.GetString() == threadId))
                    return new(CodexSendStatus.Rejected,
                        "Эта сессия не открыта в указанном процессе Codex CLI. Сообщение не отправлено.");
            }

            string status;
            using (var metadata = await RequestAsync(socket, 4,
                new { method = "thread/read", id = 4, @params = new { threadId, includeTurns = false } }, token))
            {
                var thread = RequireThreadId(RequireResult(metadata.RootElement), threadId);
                if (OptionalString(thread, "source") != "cli")
                    return new(CodexSendStatus.Rejected,
                        "Выбранная сессия не принадлежит Codex CLI. Сообщение не отправлено.");
                status = RequiredString(thread.GetProperty("status"), "type");
            }

            object dispatch;
            string? expectedTurnId = null;
            if (status == "active")
            {
                expectedTurnId = await ReadActiveTurnIdAsync(socket, threadId, token);
                if (expectedTurnId is null)
                    return new(CodexSendStatus.Rejected,
                        "Текущий ход Codex не определён. Сообщение не отправлено.");
                dispatch = new
                {
                    method = "turn/steer", id = 7,
                    @params = new
                    {
                        threadId,
                        input = new[] { new { type = "text", text = CodexQueueSender.WithConnectorPrefix(message) } },
                        expectedTurnId
                    }
                };
            }
            else if (status == "idle")
            {
                dispatch = new
                {
                    method = "turn/start", id = 7,
                    @params = new
                    {
                        threadId,
                        input = new[] { new { type = "text", text = CodexQueueSender.WithConnectorPrefix(message) } }
                    }
                };
            }
            else
                return new(CodexSendStatus.Rejected,
                    "Сессия Codex сейчас недоступна для ввода. Сообщение не отправлено.");

            // Once dispatch begins, even a failed write may have reached Codex.
            dispatchMayHaveReachedServer = true;
            await WriteAsync(socket, dispatch, token);
            using var response = await ReadResponseAsync(socket, 7, token);
            if (HasError(response.RootElement))
                return new(CodexSendStatus.Rejected, "Codex отклонил сообщение.");
            var result = RequireResult(response.RootElement);
            if (status == "active")
            {
                var actual = RequiredString(result, "turnId");
                if (actual != expectedTurnId)
                    return new(CodexSendStatus.Unknown,
                        "Codex вернул другой ход. Проверьте сессию перед повторной отправкой.");
                return new(CodexSendStatus.Accepted, "Сообщение принято Codex.", actual);
            }
            else
            {
                var actual = RequiredString(result.GetProperty("turn"), "id");
                if (OptionalString(result, "threadId") is { } responseThreadId
                    && responseThreadId != threadId)
                    return new(CodexSendStatus.Unknown,
                        "Codex вернул другую сессию. Проверьте доставку перед повторной отправкой.");
                return new(CodexSendStatus.Accepted, "Сообщение принято Codex.", actual);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return dispatchMayHaveReachedServer
                ? new(CodexSendStatus.Unknown,
                    "Codex не подтвердил приём. Сообщение могло дойти — проверьте сессию перед повтором.")
                : new(CodexSendStatus.Rejected,
                    "Прямая отправка остановлена до передачи сообщения. Проверьте соединение и сессию.");
        }
    }

    internal static async Task<ClientWebSocket> ConnectAsync(
        Uri endpoint, string? tokenFilePath, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        try
        {
            // Avoid routing a loopback bearer through an OS-configured proxy.
            socket.Options.Proxy = new WebProxy();
            if (!string.IsNullOrWhiteSpace(tokenFilePath))
            {
                var bearer = await ReadBearerAsync(tokenFilePath, token);
                socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
            }
            await socket.ConnectAsync(endpoint, token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadBearerAsync(string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Bearer token path must be absolute.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaxTokenBytes)
            throw new InvalidDataException("Bearer token file is too large.");
        var buffer = new byte[MaxTokenBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await file.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxTokenBytes) throw new InvalidDataException("Bearer token file is too large.");
        var bearer = new UTF8Encoding(false, true).GetString(buffer, 0, count).TrimEnd('\r', '\n');
        if (bearer.Length == 0 || bearer.Any(char.IsWhiteSpace)
            || bearer.Any(char.IsControl) || bearer[0] == '\uFEFF')
            throw new InvalidDataException("Bearer token file is invalid.");
        return bearer;
    }

    private static object InitializeRequest() => new
    {
        method = "initialize", id = 1,
        @params = new
        {
            clientInfo = new { name = "codex_voice", title = "Codex Voice", version = "0.1.0" },
            capabilities = new { experimentalApi = true }
        }
    };

    private static async Task<string?> ReadActiveTurnIdAsync(
        WebSocket socket, string threadId, CancellationToken token)
    {
        using (var page = await RequestAsync(socket, 5, new
        {
            method = "thread/turns/list", id = 5,
            @params = new { threadId, limit = 1, sortDirection = "desc", itemsView = "notLoaded" }
        }, token))
        {
            if (!HasError(page.RootElement))
            {
                var data = RequireResult(page.RootElement).GetProperty("data");
                var found = ActiveTurnId(data);
                if (found is not null) return found;
            }
        }

        // The fallback is bounded by the WebSocket message cap. It never submits input.
        using var fallback = await RequestAsync(socket, 6,
            new { method = "thread/read", id = 6, @params = new { threadId, includeTurns = true } }, token);
        var thread = RequireThreadId(RequireResult(fallback.RootElement), threadId);
        return ActiveTurnId(thread.GetProperty("turns"));
    }

    private static string? ActiveTurnId(JsonElement turns)
    {
        if (turns.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid Codex turn list.");
        foreach (var turn in turns.EnumerateArray())
            if (OptionalString(turn, "status") == "inProgress"
                && OptionalString(turn, "id") is { Length: > 0 } id)
                return id;
        return null;
    }

    private static JsonElement RequireThreadId(JsonElement result, string expected)
    {
        var thread = result.GetProperty("thread");
        if (RequiredString(thread, "id") != expected)
            throw new InvalidDataException("Codex returned another thread.");
        return thread;
    }

    private static string RequiredString(JsonElement value, string property) =>
        OptionalString(value, property) is { Length: > 0 } text
            ? text : throw new InvalidDataException("Required Codex property is missing.");

    private static string? OptionalString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var child)
        && child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private static JsonElement RequireResult(JsonElement response)
    {
        if (HasError(response)) throw new InvalidOperationException("Codex rejected a request.");
        if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Codex did not return a result.");
        return result;
    }

    private static bool HasError(JsonElement response) =>
        response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;

    private static async Task<JsonDocument> RequestAsync(
        WebSocket socket, int id, object request, CancellationToken token)
    {
        await WriteAsync(socket, request, token);
        return await ReadResponseAsync(socket, id, token);
    }

    private static Task WriteAsync(WebSocket socket, object request, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        return socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).AsTask();
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        WebSocket socket, int expectedId, CancellationToken token)
    {
        for (var count = 0; count < MaxProtocolMessages; count++)
        {
            using var response = await ReadMessageAsync(socket, token);
            var root = response.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt32(out var receivedId) && receivedId == expectedId)
                return JsonDocument.Parse(root.GetRawText());
        }
        throw new InvalidDataException("Codex sent too many unrelated messages.");
    }

    private static async Task<JsonDocument> ReadMessageAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var payload = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new EndOfStreamException("Codex closed the WebSocket.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Codex sent a non-text frame.");
            if (payload.Length + result.Count > MaxMessageBytes)
                throw new InvalidDataException("Codex response exceeded the message limit.");
            payload.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return JsonDocument.Parse(payload.ToArray());
        }
    }
}
