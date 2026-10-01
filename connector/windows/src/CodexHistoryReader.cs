using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexVoice;

internal sealed record CodexHistoryMessage(
    string ItemId, string Role, string Text, bool Truncated, bool HasAttachments);

internal sealed record CodexHistoryTurn(
    string TurnId, string Status, long? StartedAtUnixSeconds,
    IReadOnlyList<CodexHistoryMessage> Messages);

internal sealed record CodexHistoryPage(
    IReadOnlyList<CodexHistoryTurn> Turns, string? NextCursor, bool UsedFallback);

internal static class CodexHistoryReader
{
    private const int MaxPageSize = 50;
    private const int MaxCursorChars = 2048;
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private const int MaxTextChars = 100_000;
    private const int MaxProtocolMessages = 32;
    private const string FallbackCursorPrefix = "fallback:v1:";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public static async Task<CodexHistoryPage> ReadPageAsync(
        string threadId, int pageSize = 20, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(threadId, out var parsedId))
            throw new ArgumentException("Ожидался UUID сессии Codex.", nameof(threadId));
        if (pageSize is < 1 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Размер страницы: 1–{MaxPageSize}.");
        if (cursor is { Length: > MaxCursorChars })
            throw new ArgumentException("Курсор истории слишком длинный.", nameof(cursor));

        var fallbackOffset = ParseFallbackOffset(cursor);
        var executable = CodexQueueSender.FindExecutable()
            ?? throw new FileNotFoundException("Codex CLI не найден на этом ПК.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }
        };
        process.StartInfo.ArgumentList.Add("app-server");

        try
        {
            if (!process.Start()) throw new InvalidOperationException("Codex App Server не запустился.");
            var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var lines = new BoundedLineReader(process.StandardOutput.BaseStream);
            var input = process.StandardInput;
            var token = timeout.Token;

            using (var initialized = await RequestAsync(input, lines, 1,
                new
                {
                    method = "initialize",
                    id = 1,
                    @params = new
                    {
                        clientInfo = new { name = "codex_voice", title = "Codex Voice", version = "0.1.0" },
                        capabilities = new { experimentalApi = true }
                    }
                }, token))
            {
                RequireResult(initialized.RootElement);
            }
            await WriteAsync(input, new { method = "initialized", @params = new { } }, token);

            var id = parsedId.ToString("D");
            using (var metadata = await RequestAsync(input, lines, 2,
                new { method = "thread/read", id = 2, @params = new { threadId = id, includeTurns = false } }, token))
            {
                var thread = RequireResult(metadata.RootElement).GetProperty("thread");
                ValidateThreadIdentity(thread, id);
            }

            CodexHistoryPage page;
            if (fallbackOffset is not null)
            {
                page = await ReadFallbackAsync(input, lines, id, pageSize, fallbackOffset.Value, 3, token);
            }
            else
            {
                using var paginated = await RequestAsync(input, lines, 3,
                    new
                    {
                        method = "thread/turns/list",
                        id = 3,
                        @params = new
                        {
                            threadId = id,
                            limit = pageSize,
                            cursor,
                            sortDirection = "desc",
                            itemsView = "summary"
                        }
                    }, token);
                if (TryGetError(paginated.RootElement, out var error))
                {
                    if (!IsUnsupported(error)) throw new InvalidOperationException($"Codex App Server: {error.Message}");
                    if (cursor is not null)
                        throw new InvalidOperationException("Курсор этой версии Codex больше недоступен. Загрузите историю заново.");
                    page = await ReadFallbackAsync(input, lines, id, pageSize, 0, 4, token);
                }
                else
                {
                    var result = RequireResult(paginated.RootElement);
                    page = new CodexHistoryPage(
                        ParseTurns(result.GetProperty("data")),
                        CheckedCursor(OptionalString(result, "nextCursor")),
                        UsedFallback: false);
                }
            }

            process.StandardInput.Close();
            try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (TimeoutException) { /* The process is stopped in finally. */ }
            return page;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Codex App Server не ответил за 20 секунд.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* The short-lived reader must not affect the running CLI session. */ }
        }
    }

    private static async Task<CodexHistoryPage> ReadFallbackAsync(
        StreamWriter input, BoundedLineReader lines, string threadId,
        int pageSize, int offset, int requestId, CancellationToken token)
    {
        using var response = await RequestAsync(input, lines, requestId,
            new { method = "thread/read", id = requestId, @params = new { threadId, includeTurns = true } }, token);
        var thread = RequireResult(response.RootElement).GetProperty("thread");
        ValidateThreadIdentity(thread, threadId);
        return ParseFallbackPage(thread, pageSize, offset);
    }

    internal static void ValidateThreadIdentity(JsonElement thread, string threadId)
    {
        // source records where a thread was created. A vscode or exec thread can
        // later be resumed in the selected CLI tab; only its exact UUID identifies it.
        if (!string.Equals(RequiredString(thread, "id"), threadId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex вернул историю другой сессии.");
    }

    internal static CodexHistoryPage ParseFallbackPage(JsonElement thread, int pageSize, int offset)
    {
        var turns = ParseTurns(thread.GetProperty("turns"))
            .Select((turn, index) => (Turn: turn, Index: index))
            .OrderByDescending(entry => entry.Turn.StartedAtUnixSeconds ?? long.MinValue)
            .ThenByDescending(entry => entry.Index)
            .Select(entry => entry.Turn)
            .ToArray();
        if (offset > turns.Length) throw new ArgumentException("Курсор истории устарел.");
        var page = turns.Skip(offset).Take(pageSize).ToArray();
        var next = offset + page.Length < turns.Length
            ? FallbackCursorPrefix + (offset + page.Length).ToString(CultureInfo.InvariantCulture)
            : null;
        return new CodexHistoryPage(page, next, UsedFallback: true);
    }

    private static int? ParseFallbackOffset(string? cursor)
    {
        if (cursor is null || !cursor.StartsWith(FallbackCursorPrefix, StringComparison.Ordinal)) return null;
        var value = cursor[FallbackCursorPrefix.Length..];
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset < 0 || offset > 100_000)
            throw new ArgumentException("Некорректный курсор истории.", nameof(cursor));
        return offset;
    }

    private static string? CheckedCursor(string? cursor)
    {
        if (cursor is { Length: > MaxCursorChars })
            throw new InvalidDataException("Codex App Server вернул слишком длинный курсор.");
        return cursor;
    }

    private static IReadOnlyList<CodexHistoryTurn> ParseTurns(JsonElement turns)
    {
        if (turns.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Codex App Server вернул неверный список ходов.");
        return turns.EnumerateArray().Select(ParseTurn).ToArray();
    }

    internal static CodexHistoryTurn ParseTurn(JsonElement turn)
    {
        var messages = new List<CodexHistoryMessage>();
        if (turn.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var type = OptionalString(item, "type");
                if (type == "userMessage")
                {
                    var parts = new List<string>();
                    var hasAttachments = false;
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (OptionalString(part, "type") == "text")
                            {
                                var text = OptionalString(part, "text");
                                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                            }
                            else hasAttachments = true;
                        }
                    }
                    var raw = parts.Count > 0 ? string.Join("\n", parts) : OptionalString(item, "text") ?? "";
                    messages.Add(CreateMessage(item, "user", raw, hasAttachments));
                }
                else if (type == "agentMessage" && OptionalString(item, "phase") == "final_answer")
                {
                    messages.Add(CreateMessage(item, "assistant", OptionalString(item, "text") ?? "", false));
                }
            }
        }
        return new CodexHistoryTurn(
            RequiredString(turn, "id"),
            OptionalString(turn, "status") ?? "unknown",
            OptionalInt64(turn, "startedAt"),
            messages);
    }

    private static CodexHistoryMessage CreateMessage(JsonElement item, string role, string raw, bool hasAttachments)
    {
        var truncated = raw.Length > MaxTextChars;
        return new CodexHistoryMessage(
            OptionalString(item, "id") ?? "",
            role,
            truncated ? raw[..MaxTextChars] : raw,
            truncated,
            hasAttachments);
    }

    private static string RequiredString(JsonElement value, string name) =>
        OptionalString(value, name) ?? throw new InvalidDataException($"Codex App Server: отсутствует {name}.");

    private static string? OptionalString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? OptionalInt64(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var number)
            ? number
            : null;

    private sealed record AppServerError(int Code, string Message);

    private static bool TryGetError(JsonElement root, out AppServerError error)
    {
        error = new AppServerError(0, "Неизвестная ошибка.");
        if (!root.TryGetProperty("error", out var value) || value.ValueKind != JsonValueKind.Object)
            return false;
        var code = value.TryGetProperty("code", out var number)
            && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var parsed)
            ? parsed : 0;
        var message = OptionalString(value, "message") ?? "Неизвестная ошибка.";
        error = new AppServerError(code, message.Length > 500 ? message[..500] : message);
        return true;
    }

    private static bool IsUnsupported(AppServerError error) =>
        error.Code == -32601
        || error.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
        || error.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
        || error.Message.Contains("experimentalApi", StringComparison.OrdinalIgnoreCase);

    private static JsonElement RequireResult(JsonElement root)
    {
        if (TryGetError(root, out var error))
            throw new InvalidOperationException($"Codex App Server: {error.Message}");
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Codex App Server вернул ответ без result.");
        return result;
    }

    private static async Task<JsonDocument> RequestAsync(
        StreamWriter input, BoundedLineReader lines, int requestId, object request, CancellationToken token)
    {
        await WriteAsync(input, request, token);
        for (var count = 0; count < MaxProtocolMessages; count++)
        {
            var bytes = await lines.ReadLineAsync(MaxResponseBytes, token);
            var response = JsonDocument.Parse(bytes);
            if (response.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value)
                && value == requestId)
                return response;
            response.Dispose();
        }
        throw new InvalidDataException("Codex App Server прислал слишком много посторонних событий.");
    }

    private static async Task WriteAsync(StreamWriter input, object request, CancellationToken token)
    {
        await input.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
        await input.FlushAsync(token);
    }

    private sealed class BoundedLineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _position;
        private int _count;

        public async Task<byte[]> ReadLineAsync(int maxBytes, CancellationToken token)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (_position == _count)
                {
                    _count = await stream.ReadAsync(_buffer.AsMemory(), token);
                    _position = 0;
                    if (_count == 0) throw new EndOfStreamException("Codex App Server закрыл соединение.");
                }
                var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _count - _position);
                var end = newline < 0 ? _count : newline;
                var length = end - _position;
                if (line.Length + length > maxBytes)
                    throw new InvalidDataException("Ответ Codex App Server превышает допустимый размер.");
                line.Write(_buffer, _position, length);
                _position = newline < 0 ? _count : newline + 1;
                if (newline >= 0) return line.ToArray();
            }
        }
    }
}
