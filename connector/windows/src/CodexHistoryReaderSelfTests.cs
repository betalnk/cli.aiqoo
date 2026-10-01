using System.Text.Json;

namespace CodexVoice;

internal static class CodexHistoryReaderSelfTests
{
    public static void Run()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "turn-1",
              "status": "completed",
              "startedAt": 1720000000,
              "items": [
                { "type": "userMessage", "id": "user-1", "content": [
                  { "type": "text", "text": "Первая строка" },
                  { "type": "text", "text": "Вторая строка" },
                  { "type": "image", "url": "ignored" }
                ] },
                { "type": "agentMessage", "id": "draft-1", "phase": "commentary", "text": "Промежуточный ответ" },
                { "type": "agentMessage", "id": "final-1", "phase": "final_answer", "text": "Итоговый ответ" }
              ]
            }
            """);

        var turn = CodexHistoryReader.ParseTurn(document.RootElement);
        Check(turn.TurnId == "turn-1" && turn.Status == "completed");
        Check(turn.StartedAtUnixSeconds == 1720000000);
        Check(turn.Messages.Count == 2);
        Check(turn.Messages[0] is { Role: "user", Text: "Первая строка\nВторая строка", HasAttachments: true });
        Check(turn.Messages[1] is { Role: "assistant", Text: "Итоговый ответ", HasAttachments: false });

        using var fallback = JsonDocument.Parse("""
            {
              "source": "cli",
              "turns": [
                { "id": "older", "status": "completed", "startedAt": 10, "items": [] },
                { "id": "newer", "status": "completed", "startedAt": 20, "items": [] }
              ]
            }
            """);
        var first = CodexHistoryReader.ParseFallbackPage(fallback.RootElement, 1, 0);
        var second = CodexHistoryReader.ParseFallbackPage(fallback.RootElement, 1, 1);
        Check(first.UsedFallback && first.Turns.Single().TurnId == "newer");
        Check(first.NextCursor == "fallback:v1:1");
        Check(second.Turns.Single().TurnId == "older" && second.NextCursor is null);

        var threadId = Guid.NewGuid().ToString("D");
        foreach (var source in new[] { "cli", "vscode", "exec" })
        {
            using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(new { id = threadId, source }));
            CodexHistoryReader.ValidateThreadIdentity(metadata.RootElement, threadId);
            CodexHistoryReader.ValidateThreadIdentity(metadata.RootElement, threadId.ToUpperInvariant());
            var rejected = false;
            try { CodexHistoryReader.ValidateThreadIdentity(metadata.RootElement, Guid.NewGuid().ToString("D")); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected);
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Codex history parser self-test failed.");
    }
}
