using System.Diagnostics;
using System.Text.Json;

namespace CodexVoice;

internal sealed record CodexReceiptBaseline(string RolloutPath, long Offset);

/// <summary>
/// Confirms a terminal keystroke by observing a new user message in the exact
/// Codex rollout. A successful SendInput call alone is never a Codex receipt.
/// </summary>
internal static class CodexRolloutReceiptVerifier
{
    private const int MaxNewBytes = 1024 * 1024;
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(8);

    internal static bool TryCapture(string threadId, out CodexReceiptBaseline? baseline,
        out string error)
    {
        baseline = null;
        if (!SessionResolver.TryGetRolloutPath(threadId, out var path))
        {
            error = "Не найден журнал выбранной сессии Codex. Текст не отправлен.";
            return false;
        }
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var offset = file.Length;
            if (offset > 0)
            {
                file.Position = offset - 1;
                if (file.ReadByte() != '\n')
                {
                    error = "Журнал Codex ещё записывается. Повторите отправку через секунду.";
                    return false;
                }
            }
            baseline = new CodexReceiptBaseline(path, offset);
            error = "";
            return true;
        }
        catch (IOException)
        {
            error = "Не удалось прочитать журнал выбранной сессии. Текст не отправлен.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Нет доступа к журналу выбранной сессии. Текст не отправлен.";
            return false;
        }
    }

    internal static async Task<bool> WaitForUserMessageAsync(CodexReceiptBaseline baseline,
        string expectedText, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        var position = baseline.Offset;
        using var line = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (watch.Elapsed < ReceiptTimeout)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var file = new FileStream(baseline.RolloutPath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length < position || file.Length - baseline.Offset > MaxNewBytes)
                    return false;
                file.Position = position;
                int count;
                while ((count = await file.ReadAsync(buffer.AsMemory(), token)) > 0)
                {
                    position += count;
                    for (var index = 0; index < count; index++)
                    {
                        if (buffer[index] == '\n')
                        {
                            if (IsMatchingUserMessage(line, expectedText)) return true;
                            line.SetLength(0);
                        }
                        else
                        {
                            line.WriteByte(buffer[index]);
                            if (line.Length > MaxNewBytes) return false;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Codex can briefly replace or lock a rollout during startup.
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            await Task.Delay(180, token);
        }
        return false;
    }

    private static bool IsMatchingUserMessage(MemoryStream line, string expectedText)
    {
        if (line.Length == 0) return false;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (!StringPropertyEquals(root, "type", "response_item")
                || !root.TryGetProperty("payload", out var payload)
                || !StringPropertyEquals(payload, "type", "message")
                || !StringPropertyEquals(payload, "role", "user")
                || !payload.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in content.EnumerateArray())
            {
                if (StringPropertyEquals(item, "type", "input_text")
                    && item.TryGetProperty("text", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString()?.TrimEnd('\r', '\n'),
                        expectedText.TrimEnd('\r', '\n'), StringComparison.Ordinal))
                    return true;
            }
        }
        catch (JsonException)
        {
            // Ignore unrelated malformed or in-progress log lines.
        }
        return false;
    }

    private static bool StringPropertyEquals(JsonElement value, string property, string expected)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(property, out var field)
           && field.ValueKind == JsonValueKind.String
           && string.Equals(field.GetString(), expected, StringComparison.Ordinal);
}
