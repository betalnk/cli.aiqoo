using System.Net.WebSockets;
using System.Text.Json;

namespace CodexVoice;

/// <summary>Metadata confirmed by a loaded Codex CLI thread on the configured app-server.</summary>
internal sealed record LoadedThreadIdentity(string ThreadId, string Cwd, string? Name);

internal sealed record CodexLoadedThreadResolution(LoadedThreadIdentity? Identity, string Error)
{
    internal bool Success => Identity is not null;
}

/// <summary>
/// Resolves Codex's truncated terminal title to one loaded CLI thread without sending input
/// or opening an unloaded thread. The caller must separately verify the foreground window.
/// </summary>
internal static class CodexLoadedThreadResolver
{
    private const int PrefixLength = 29;
    private const int DisplayLength = PrefixLength + 3;
    private const int MaxMessageBytes = 1024 * 1024;
    private const int MaxProtocolMessages = 64;
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    internal static async Task<CodexLoadedThreadResolution> ResolveAsync(
        CodexWebSocketConfig config, string? displayedId, CancellationToken cancellationToken = default)
    {
        if (!TryGetDisplayedPrefix(displayedId, out var prefix))
            return new(null, "Заголовок Codex не содержит усечённый ID сессии.");
        if (!CodexWebSocketSender.TryGetEndpoint(config, out var endpoint))
            return new(null, "Не задан защищённый локальный WebSocket Codex.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);
        try
        {
            // Reuse the sender's loopback proxy and bearer-token policy. Never include
            // exception text or server-provided strings in a caller-visible error.
            using var socket = await CodexWebSocketSender.ConnectAsync(
                endpoint!, config.TokenFilePath, timeout.Token);
            return await ResolveOverProtocolAsync(socket, prefix, timeout.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(null, "Не удалось проверить открытую сессию через Codex app-server.");
        }
    }

    /// <summary>Read-only protocol core for isolated fake-server tests.</summary>
    internal static async Task<CodexLoadedThreadResolution> ResolveOverProtocolAsync(
        WebSocket socket, string prefix, CancellationToken cancellationToken = default)
    {
        if (!IsPrefix(prefix))
            return new(null, "Неверный префикс ID сессии Codex.");

        try
        {
            using (var hello = await RequestAsync(socket, 1, new
            {
                method = "initialize", id = 1,
                @params = new
                {
                    clientInfo = new { name = "codex_voice", title = "Codex Voice", version = "0.1.0" },
                    capabilities = new { experimentalApi = true }
                }
            }, cancellationToken))
                RequireResult(hello.RootElement);

            await WriteAsync(socket, new { method = "initialized", @params = new { } }, cancellationToken);

            string? threadId = null;
            using (var loaded = await RequestAsync(socket, 2,
                new { method = "thread/loaded/list", id = 2, @params = new { } }, cancellationToken))
            {
                var result = RequireResult(loaded.RootElement);
                if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Invalid loaded thread list.");

                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var candidate = item.GetString();
                    if (SessionResolver.TryCanonicalThreadId(candidate, out var canonical)
                        && canonical.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        if (threadId is not null)
                            return new(null, "Усечённый ID соответствует нескольким открытым сессиям Codex.");
                        threadId = canonical;
                    }
                }
            }

            if (threadId is null)
                return new(null, "Сессия из заголовка не открыта в указанном процессе Codex.");

            using var metadata = await RequestAsync(socket, 3,
                new { method = "thread/read", id = 3,
                    @params = new { threadId, includeTurns = false } }, cancellationToken);
            var threadResult = RequireResult(metadata.RootElement);
            if (!threadResult.TryGetProperty("thread", out var thread)
                || thread.ValueKind != JsonValueKind.Object
                || OptionalString(thread, "id") != threadId)
                return new(null, "Codex вернул данные другой сессии.");
            if (OptionalString(thread, "source") != "cli")
                return new(null, "Открытая сессия не принадлежит Codex CLI.");

            var cwd = OptionalString(thread, "cwd");
            if (string.IsNullOrWhiteSpace(cwd) || cwd.Length > 4096 || cwd.Any(char.IsControl)
                || !Path.IsPathFullyQualified(cwd))
                return new(null, "Codex не вернул рабочий каталог открытой сессии.");

            var name = OptionalString(thread, "name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Any(char.IsControl))
                name = null;
            return new(new LoadedThreadIdentity(threadId, cwd, name), "");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(null, "Не удалось проверить открытую сессию через Codex app-server.");
        }
    }

    private static bool TryGetDisplayedPrefix(string? displayedId, out string prefix)
    {
        prefix = "";
        if (displayedId is not { Length: DisplayLength }
            || !displayedId.EndsWith("...", StringComparison.Ordinal)) return false;
        var candidate = displayedId[..PrefixLength];
        if (!IsPrefix(candidate)) return false;
        prefix = candidate;
        return true;
    }

    private static bool IsPrefix(string? prefix)
    {
        if (prefix is not { Length: PrefixLength }) return false;
        for (var index = 0; index < PrefixLength; index++)
        {
            var character = prefix[index];
            if (index is 8 or 13 or 18 or 23)
            {
                if (character != '-') return false;
            }
            else if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static string? OptionalString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var child)
        && child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private static JsonElement RequireResult(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
            || !response.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Codex request failed.");
        return result;
    }

    private static async Task<JsonDocument> RequestAsync(
        WebSocket socket, int id, object request, CancellationToken token)
    {
        await WriteAsync(socket, request, token);
        for (var count = 0; count < MaxProtocolMessages; count++)
        {
            using var response = await ReadMessageAsync(socket, token);
            var root = response.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.TryGetInt32(out var actualId) && actualId == id)
                return JsonDocument.Parse(root.GetRawText());
        }
        throw new InvalidDataException("Too many unrelated Codex messages.");
    }

    private static Task WriteAsync(WebSocket socket, object request, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        return socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).AsTask();
    }

    private static async Task<JsonDocument> ReadMessageAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var payload = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (frame.MessageType == WebSocketMessageType.Close)
                throw new EndOfStreamException("Codex closed the WebSocket.");
            if (frame.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Non-text Codex response.");
            if (payload.Length + frame.Count > MaxMessageBytes)
                throw new InvalidDataException("Codex response exceeded the message limit.");
            payload.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage)
                return JsonDocument.Parse(payload.ToArray());
        }
    }
}
