using System.Net.WebSockets;
using System.Text.Json;

namespace CodexVoice;

internal enum CodexHookTrustStatus
{
    Trusted,
    NotTrusted,
    Unavailable
}

internal sealed record CodexHookTrustResult(CodexHookTrustStatus Status, string Message)
{
    internal bool Trusted => Status == CodexHookTrustStatus.Trusted;
}

/// <summary>
/// Reads the current app-server hook inventory for one working directory. This client never
/// changes hook trust, Codex configuration, threads, or plugin files.
/// </summary>
internal static class CodexHookTrustProbe
{
    private const int MaxMessageBytes = 256 * 1024;
    private const int MaxNotifications = 16;
    private const int MaxHooks = 128;
    private const int MaxManifestBytes = 16 * 1024;
    private const string PluginId = "codex-voice@cli";
    private const string HookCommand = "CodexVoice.exe --register-from-hook";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };

    private static readonly CodexHookTrustResult Unavailable = new(
        CodexHookTrustStatus.Unavailable, "Не удалось проверить hooks Codex Voice.");
    private static readonly CodexHookTrustResult NotTrusted = new(
        CodexHookTrustStatus.NotTrusted, "Hooks Codex Voice не подтверждены как доверенные и включённые.");
    private static readonly CodexHookTrustResult Trusted = new(
        CodexHookTrustStatus.Trusted, "Hooks Codex Voice доверены и включены.");

    internal static async Task<CodexHookTrustResult> ProbeAsync(
        CodexWebSocketConfig config, string cwd, CancellationToken token = default)
    {
        if (!CodexWebSocketSender.TryGetEndpoint(config, out var endpoint)
            || !TryNormalizeAbsolutePath(cwd, out var normalizedCwd)
            || normalizedCwd.Length > 4096)
            return Unavailable;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            if (!TryFindInstalledHooksPath(out var hooksPath)) return NotTrusted;
            using var socket = await CodexWebSocketSender.ConnectAsync(
                endpoint!, config.TokenFilePath, timeout.Token);
            return await ProbeOverProtocolAsync(socket, normalizedCwd, hooksPath, timeout.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Server data and exception text may echo the bearer. Never expose either.
            return Unavailable;
        }
    }

    /// <summary>Protocol core, also exercised by a loopback fake server.</summary>
    internal static async Task<CodexHookTrustResult> ProbeOverProtocolAsync(
        WebSocket socket, string cwd, string installedHooksPath, CancellationToken token = default)
    {
        await WriteAsync(socket, new
        {
            method = "initialize", id = 1,
            @params = new
            {
                clientInfo = new { name = "codex_voice", title = "Codex Voice", version = "0.1.0" },
                capabilities = new { experimentalApi = true }
            }
        }, token);
        using (var hello = await ReadResponseAsync(socket, 1, token))
            RequireResult(hello.RootElement);
        await WriteAsync(socket, new { method = "initialized", @params = new { } }, token);
        await WriteAsync(socket, new
        {
            method = "hooks/list", id = 2,
            @params = new { cwds = new[] { cwd } }
        }, token);
        using var response = await ReadResponseAsync(socket, 2, token);
        return EvaluateResponse(response.RootElement, cwd, installedHooksPath);
    }

    /// <summary>Strict, side-effect-free interpretation of one hooks/list response.</summary>
    internal static CodexHookTrustResult EvaluateResponse(
        JsonElement response, string cwd, string installedHooksPath)
    {
        try
        {
            var result = RequireResult(response);
            var data = RequireArray(result, "data");
            if (data.GetArrayLength() != 1) return NotTrusted;

            var entry = data[0];
            if (entry.ValueKind != JsonValueKind.Object
                || !SamePath(RequireString(entry, "cwd"), cwd)
                || RequireArray(entry, "errors").GetArrayLength() != 0
                || RequireArray(entry, "warnings").GetArrayLength() != 0)
                return NotTrusted;

            var hooks = RequireArray(entry, "hooks");
            if (hooks.GetArrayLength() > MaxHooks) return NotTrusted;

            var pluginCache = Path.GetDirectoryName(Path.GetDirectoryName(
                Path.GetDirectoryName(installedHooksPath)))!;
            var foundStart = false;
            var foundSubmit = false;
            foreach (var hook in hooks.EnumerateArray())
            {
                if (hook.ValueKind != JsonValueKind.Object) return NotTrusted;
                var eventName = RequireString(hook, "eventName");
                var key = RequireString(hook, "key");
                var sourcePath = RequireString(hook, "sourcePath");
                var handlerType = RequireString(hook, "handlerType");
                var command = handlerType == "command" ? RequireString(hook, "command") : null;
                var pluginId = OptionalString(hook, "pluginId");

                // Include suspicious lookalikes in the count. A user hook with the same command,
                // or a second plugin hook from another version, makes identity ambiguous.
                var candidate = pluginId == PluginId
                    || key.StartsWith(PluginId + ":", StringComparison.Ordinal)
                    || command == HookCommand
                    || IsWithin(sourcePath, pluginCache);
                if (!candidate) continue;

                if (!SamePath(sourcePath, installedHooksPath)
                    || RequireString(hook, "source") != "plugin"
                    || pluginId is not null && pluginId != PluginId
                    || handlerType != "command"
                    || command != HookCommand
                    || !RequireBool(hook, "enabled")
                    || RequireString(hook, "trustStatus") != "trusted"
                    || hook.TryGetProperty("async", out var asyncValue)
                        && asyncValue.ValueKind != JsonValueKind.False)
                    return NotTrusted;

                if (eventName == "sessionStart")
                {
                    if (foundStart) return NotTrusted;
                    foundStart = true;
                }
                else if (eventName == "userPromptSubmit")
                {
                    if (foundSubmit) return NotTrusted;
                    foundSubmit = true;
                }
                else return NotTrusted;
            }
            return foundStart && foundSubmit ? Trusted : NotTrusted;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return NotTrusted;
        }
    }

    private static bool TryFindInstalledHooksPath(out string hooksPath)
    {
        hooksPath = string.Empty;
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        if (!TryNormalizeAbsolutePath(codexHome, out var home)) return false;
        var pluginCache = Path.Combine(home, "plugins", "cache", "cli", "codex-voice");
        if (!Directory.Exists(pluginCache)) return false;

        // A second cached version makes the path-to-active-plugin mapping ambiguous.
        var versions = Directory.EnumerateDirectories(pluginCache).Take(2).ToArray();
        if (versions.Length != 1) return false;
        var versionRoot = versions[0];
        var manifestPath = Path.Combine(versionRoot, ".codex-plugin", "plugin.json");
        var candidate = Path.Combine(versionRoot, "hooks", "hooks.json");
        if (!File.Exists(manifestPath) || !File.Exists(candidate)
            || new FileInfo(manifestPath).Length > MaxManifestBytes)
            return false;

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath), JsonOptions);
        if (RequireString(manifest.RootElement, "name") != "codex-voice"
            || RequireString(manifest.RootElement, "version") != Path.GetFileName(versionRoot))
            return false;
        hooksPath = Path.GetFullPath(candidate);
        return true;
    }

    private static bool SamePath(string left, string right) =>
        TryNormalizeAbsolutePath(left, out var normalizedLeft)
        && TryNormalizeAbsolutePath(right, out var normalizedRight)
        && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string path, string directory) =>
        TryNormalizeAbsolutePath(path, out var normalizedPath)
        && TryNormalizeAbsolutePath(directory, out var normalizedDirectory)
        && normalizedPath.StartsWith(
            Path.TrimEndingDirectorySeparator(normalizedDirectory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeAbsolutePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return true;
    }

    private static JsonElement RequireResult(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || response.TryGetProperty("error", out var error)
                && error.ValueKind != JsonValueKind.Null
            || !response.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid Codex response.");
        return result;
    }

    private static JsonElement RequireArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid Codex hook inventory.");
        return array;
    }

    private static string RequireString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var child)
        && child.ValueKind == JsonValueKind.String
        && child.GetString() is { } text
            ? text : throw new InvalidDataException("Invalid Codex hook metadata.");

    private static string? OptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var child) || child.ValueKind == JsonValueKind.Null)
            return null;
        if (child.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid Codex plugin identity.");
        return child.GetString();
    }

    private static bool RequireBool(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var child)
            || child.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Invalid Codex hook state.");
        return child.GetBoolean();
    }

    private static Task WriteAsync(WebSocket socket, object request, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        return socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).AsTask();
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        WebSocket socket, int expectedId, CancellationToken token)
    {
        for (var count = 0; count <= MaxNotifications; count++)
        {
            var response = await ReadMessageAsync(socket, token);
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                response.Dispose();
                throw new InvalidDataException("Invalid Codex response.");
            }
            if (root.TryGetProperty("id", out var id))
            {
                if (id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt32(out var receivedId) && receivedId == expectedId)
                    return response;
                response.Dispose();
                throw new InvalidDataException("Unexpected Codex response ID.");
            }
            response.Dispose();
        }
        throw new InvalidDataException("Too many Codex notifications.");
    }

    private static async Task<JsonDocument> ReadMessageAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var payload = new MemoryStream();
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (part.MessageType != WebSocketMessageType.Text
                || payload.Length + part.Count > MaxMessageBytes)
                throw new InvalidDataException("Invalid Codex response frame.");
            payload.Write(buffer, 0, part.Count);
            if (part.EndOfMessage)
                return JsonDocument.Parse(payload.GetBuffer().AsMemory(0, (int)payload.Length), JsonOptions);
        }
    }
}
