using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

internal enum CodexSendStatus { Accepted, Rejected, Unknown }

internal sealed record CodexSendResult(CodexSendStatus Status, string Message, string? TurnId = null);
internal enum CodexConnectionStatus { Connected, Unavailable }
internal sealed record CodexConnectionResult(CodexConnectionStatus Status, string Message);

/// <summary>Sends to a loaded CLI thread through the same app-server daemon as its TUI.</summary>
internal static class CodexAppServerSender
{
    private const int MaxLineBytes = 32 * 1024 * 1024;
    private const int MaxProtocolMessages = 4096;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    internal static ProcessStartInfo CreateStartInfo(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("proxy");
        return start;
    }

    /// <summary>Checks only the JSON-RPC handshake, without listing or loading threads.</summary>
    public static async Task<CodexConnectionResult> ProbeConnectionAsync(CancellationToken token = default)
    {
        var executable = CodexQueueSender.FindExecutable();
        if (executable is null)
            return new(CodexConnectionStatus.Unavailable, "Не найден Codex CLI.");

        using var process = new Process { StartInfo = CreateStartInfo(executable) };
        try
        {
            if (!process.Start())
                return new(CodexConnectionStatus.Unavailable, "Не удалось запустить Codex app-server proxy.");
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ProbeTimeout);
            var result = await ProbeOverProtocolAsync(process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream, timeout.Token);
            if (result.Status == CodexConnectionStatus.Connected)
                return result;
            if (!process.HasExited)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                try { await process.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException) { }
            }
            if (!process.HasExited) return result;
            try
            {
                var detail = await stderr.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
                if (detail.Contains("10050", StringComparison.Ordinal))
                    return new(CodexConnectionStatus.Unavailable,
                        "Общий сервер Codex недоступен: управляющий сокет недоступен (Windows 10050).");
                if (!string.IsNullOrWhiteSpace(detail))
                    return new(CodexConnectionStatus.Unavailable,
                        $"Codex app-server proxy завершился с ошибкой: {detail.Trim()[..Math.Min(detail.Trim().Length, 240)]}");
            }
            catch { /* The generic unavailable result is still accurate. */ }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(CodexConnectionStatus.Unavailable,
                $"Общий сервер Codex недоступен: {exception.Message}");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* The probe owns only its short-lived proxy. */ }
        }
    }

    internal static async Task<CodexConnectionResult> ProbeOverProtocolAsync(
        Stream toServer, Stream fromServer, CancellationToken token = default)
    {
        using var writer = new StreamWriter(toServer, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            NewLine = "\n"
        };
        var reader = new BoundedLineReader(fromServer);
        try
        {
            using var hello = await RequestAsync(writer, reader, 1, InitializeRequest(), token);
            RequireResult(hello.RootElement);
            await WriteAsync(writer, new { method = "initialized", @params = new { } }, token);
            return new(CodexConnectionStatus.Connected, "Общий сервер Codex доступен.");
        }
        catch (OperationCanceledException)
        {
            return new(CodexConnectionStatus.Unavailable, "Общий сервер Codex не ответил за 3 секунды.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(CodexConnectionStatus.Unavailable,
                $"Нет подключения к общему серверу Codex: {exception.Message}");
        }
    }

    public static async Task<CodexSendResult> SendAsync(
        string threadId, string message, CancellationToken token = default)
    {
        var executable = CodexQueueSender.FindExecutable();
        if (executable is null)
            return new(CodexSendStatus.Rejected, "Не найден Codex CLI. Установите его или задайте CODEX_EXECUTABLE.");

        using var process = new Process { StartInfo = CreateStartInfo(executable) };
        try
        {
            if (!process.Start())
                return new(CodexSendStatus.Rejected, "Не удалось подключиться к Codex CLI.");
            // A busy daemon can write diagnostics while the JSON-RPC proxy is in use.
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);
            return await SendOverProtocolAsync(process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream, threadId, message, timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(CodexSendStatus.Rejected, $"Не удалось подключиться к Codex: {exception.Message}");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* Only the short-lived proxy belongs to this request. */ }
        }
    }

    /// <summary>Protocol core separated from process startup so no live Codex session is needed for tests.</summary>
    internal static async Task<CodexSendResult> SendOverProtocolAsync(
        Stream toServer, Stream fromServer, string threadId, string message, CancellationToken token = default)
    {
        if (!Guid.TryParseExact(threadId, "D", out var parsed)
            || parsed.ToString("D") != threadId || string.IsNullOrWhiteSpace(message))
            return new(CodexSendStatus.Rejected, "Неверный UUID сессии или пустое сообщение.");

        var dispatchMayHaveReachedServer = false;
        using var writer = new StreamWriter(toServer, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            NewLine = "\n"
        };
        var reader = new BoundedLineReader(fromServer);
        try
        {
            using (var hello = await RequestAsync(writer, reader, 1, InitializeRequest(), token))
                RequireResult(hello.RootElement);
            await WriteAsync(writer, new { method = "initialized", @params = new { } }, token);

            using (var loaded = await RequestAsync(writer, reader, 2,
                new { method = "thread/loaded/list", id = 2, @params = new { } }, token))
            {
                var data = RequireResult(loaded.RootElement).GetProperty("data");
                if (data.ValueKind != JsonValueKind.Array ||
                    !data.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                        && item.GetString() == threadId))
                    return new(CodexSendStatus.Rejected,
                        "Эта сессия не открыта в общем процессе Codex CLI.");
            }

            using (var resumed = await RequestAsync(writer, reader, 3,
                new { method = "thread/resume", id = 3, @params = new { threadId } }, token))
                RequireThreadId(RequireResult(resumed.RootElement), threadId);

            string status;
            using (var metadata = await RequestAsync(writer, reader, 4,
                new { method = "thread/read", id = 4, @params = new { threadId, includeTurns = false } }, token))
            {
                var thread = RequireThreadId(RequireResult(metadata.RootElement), threadId);
                if (OptionalString(thread, "source") != "cli")
                    return new(CodexSendStatus.Rejected, "Выбранная сессия не принадлежит Codex CLI.");
                var state = thread.GetProperty("status");
                status = RequiredString(state, "type");
            }

            int dispatchId;
            object dispatch;
            if (status == "active")
            {
                var activeTurnId = await ReadActiveTurnIdAsync(writer, reader, threadId, token);
                if (activeTurnId is null)
                    return new(CodexSendStatus.Rejected,
                        "Не удалось определить текущий ход Codex. Сообщение не отправлено.");
                dispatchId = 7;
                dispatch = new
                {
                    method = "turn/steer", id = dispatchId,
                    @params = new
                    {
                        threadId,
                        input = new[] { new { type = "text", text = CodexQueueSender.WithConnectorPrefix(message) } },
                        expectedTurnId = activeTurnId
                    }
                };
            }
            else if (status == "idle")
            {
                dispatchId = 7;
                dispatch = new
                {
                    method = "turn/start", id = dispatchId,
                    @params = new
                    {
                        threadId,
                        input = new[] { new { type = "text", text = CodexQueueSender.WithConnectorPrefix(message) } }
                    }
                };
            }
            else
            {
                return new(CodexSendStatus.Rejected, $"Сессия Codex недоступна для ввода ({status}).");
            }

            // A failure from this point on may occur after Codex accepted the user input.
            dispatchMayHaveReachedServer = true;
            await WriteAsync(writer, dispatch, token);
            using var response = await ReadResponseAsync(reader, dispatchId, token);
            if (TryError(response.RootElement, out var error))
                return new(CodexSendStatus.Rejected, $"Codex отклонил сообщение: {error}");
            var result = RequireResult(response.RootElement);
            string turnId;
            if (status == "active")
                turnId = RequiredString(result, "turnId");
            else
                turnId = RequiredString(result.GetProperty("turn"), "id");
            return new(CodexSendStatus.Accepted, "Сообщение принято Codex.", turnId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return dispatchMayHaveReachedServer
                ? new(CodexSendStatus.Unknown,
                    "Codex не подтвердил приём. Сообщение могло дойти — проверьте сессию перед повтором.")
                : new(CodexSendStatus.Rejected, $"Сообщение не отправлено: {exception.Message}");
        }
    }

    private static async Task<string?> ReadActiveTurnIdAsync(
        StreamWriter writer, BoundedLineReader reader, string threadId, CancellationToken token)
    {
        using (var page = await RequestAsync(writer, reader, 5,
            new
            {
                method = "thread/turns/list", id = 5,
                @params = new { threadId, limit = 1, sortDirection = "desc", itemsView = "notLoaded" }
            }, token))
        {
            if (!TryError(page.RootElement, out _))
            {
                var data = RequireResult(page.RootElement).GetProperty("data");
                var found = ActiveTurnId(data);
                if (found is not null) return found;
            }
        }

        // Older thread stores may not implement turn pagination. This fallback is
        // bounded; if the history is too large, fail before submitting any input.
        using var fallback = await RequestAsync(writer, reader, 6,
            new { method = "thread/read", id = 6, @params = new { threadId, includeTurns = true } }, token);
        var thread = RequireThreadId(RequireResult(fallback.RootElement), threadId);
        return ActiveTurnId(thread.GetProperty("turns"));
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

    private static string? ActiveTurnId(JsonElement turns)
    {
        if (turns.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex вернул неверный список ходов.");
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
            throw new InvalidDataException("Codex вернул другую сессию.");
        return thread;
    }

    private static string RequiredString(JsonElement value, string property) =>
        OptionalString(value, property) is { Length: > 0 } text
            ? text : throw new InvalidDataException($"Ответ Codex не содержит {property}.");

    private static string? OptionalString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var child)
        && child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private static JsonElement RequireResult(JsonElement response)
    {
        if (TryError(response, out var error))
            throw new InvalidOperationException(error);
        if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Codex не вернул result.");
        return result;
    }

    private static bool TryError(JsonElement response, out string error)
    {
        error = "";
        if (!response.TryGetProperty("error", out var item) || item.ValueKind != JsonValueKind.Object)
            return false;
        error = OptionalString(item, "message") ?? "Неизвестная ошибка.";
        if (error.Length > 300) error = error[..300];
        return true;
    }

    private static async Task<JsonDocument> RequestAsync(
        StreamWriter writer, BoundedLineReader reader, int id, object request, CancellationToken token)
    {
        await WriteAsync(writer, request, token);
        return await ReadResponseAsync(reader, id, token);
    }

    private static async Task WriteAsync(StreamWriter writer, object request, CancellationToken token)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
        await writer.FlushAsync(token);
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        BoundedLineReader reader, int expectedId, CancellationToken token)
    {
        for (var count = 0; count < MaxProtocolMessages; count++)
        {
            using var response = JsonDocument.Parse(await reader.ReadLineAsync(MaxLineBytes, token));
            var root = response.RootElement;
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt32(out var receivedId) && receivedId == expectedId)
                return JsonDocument.Parse(root.GetRawText());
        }
        throw new InvalidDataException("Codex прислал слишком много посторонних событий.");
    }

    private sealed class BoundedLineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _position;
        private int _count;

        internal async Task<byte[]> ReadLineAsync(int maxBytes, CancellationToken token)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (_position == _count)
                {
                    _count = await stream.ReadAsync(_buffer.AsMemory(), token);
                    _position = 0;
                    if (_count == 0) throw new EndOfStreamException("Codex закрыл соединение.");
                }
                var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _count - _position);
                var end = newline < 0 ? _count : newline;
                var length = end - _position;
                if (line.Length + length > maxBytes)
                    throw new InvalidDataException("Ответ Codex слишком велик.");
                line.Write(_buffer, _position, length);
                _position = newline < 0 ? _count : newline + 1;
                if (newline >= 0) return line.ToArray();
            }
        }
    }
}
