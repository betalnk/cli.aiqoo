using System.Text;
using System.Text.Json;

namespace CodexVoice;

/// <summary>Fake JSON-RPC server responses; these tests never launch Codex or touch a live thread.</summary>
internal static class CodexAppServerSenderSelfTests
{
    private const string ThreadId = "123e4567-e89b-42d3-a456-426614174000";
    private const string OtherThreadId = "123e4567-e89b-42d3-a456-426614174001";
    private const string TurnId = "123e4567-e89b-42d3-a456-426614174002";

    internal static async Task RunAsync()
    {
        var process = CodexAppServerSender.CreateStartInfo(@"C:\codex.exe");
        Check(!process.UseShellExecute && process.RedirectStandardInput && process.RedirectStandardOutput);
        Check(process.ArgumentList.Cast<string>().SequenceEqual(["app-server", "proxy"]));

        var healthy = await RunProbeScriptAsync(Hello());
        Check(healthy.Result.Status == CodexConnectionStatus.Connected);
        Check(healthy.Requests.Select(Method).SequenceEqual(["initialize", "initialized"]));
        var offline = await RunProbeScriptAsync();
        Check(offline.Result.Status == CodexConnectionStatus.Unavailable);
        Check(offline.Requests.Select(Method).SequenceEqual(["initialize"]));

        var queueStart = CodexQueueSender.CreateQueueStartInfo(@"C:\codex.exe", ThreadId,
            "текст && `whoami` \"quote\"");
        Check(!queueStart.UseShellExecute);
        Check(queueStart.ArgumentList.Cast<string>().SequenceEqual([
            "queue", "--thread", ThreadId, "--message",
            CodexQueueSender.ConnectorPrefix + "\nтекст && `whoami` \"quote\""]));
        var boundAt = DateTimeOffset.Parse("2026-09-28T00:00:00Z");
        var selectedBinding = new BoundSession(ThreadId, "Сессия", @"C:\work\project", boundAt);
        Check(CodexQueueSender.MatchesBinding(selectedBinding, selectedBinding));
        Check(!CodexQueueSender.MatchesBinding(selectedBinding, selectedBinding with { BoundAtUtc = boundAt.AddMinutes(1) }));
        Check(!CodexQueueSender.MatchesBinding(selectedBinding, selectedBinding with { Cwd = @"C:\work\other" }));
        var invalidQueue = await CodexQueueSender.QueueAsync(
            selectedBinding with { ThreadId = "invalid" }, "Не отправлять");
        Check(invalidQueue.Status == CodexQueueStatus.Rejected);

        var idle = await RunScriptAsync("Привет", Hello(), Loaded(ThreadId), Resumed(),
            Metadata("idle"), new { id = 7, result = new { turn = new { id = TurnId } } });
        Check(idle.Result.Status == CodexSendStatus.Accepted && idle.Result.TurnId == TurnId);
        Check(idle.Requests.Select(Method).SequenceEqual(
            ["initialize", "initialized", "thread/loaded/list", "thread/resume", "thread/read", "turn/start"]));
        var start = idle.Requests.Last();
        Check(start.GetProperty("params").GetProperty("threadId").GetString() == ThreadId);
        Check(start.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()
            == CodexQueueSender.ConnectorPrefix + "\nПривет");

        var active = await RunScriptAsync("[via Voice Connector]\nУточни", Hello(),
            Loaded(ThreadId), Resumed(), Metadata("active"),
            new { id = 5, result = new { data = new[] { new { id = TurnId, status = "inProgress" } } } },
            new { id = 7, result = new { turnId = TurnId } });
        Check(active.Result.Status == CodexSendStatus.Accepted && active.Result.TurnId == TurnId);
        Check(Method(active.Requests.Last()) == "turn/steer");
        Check(active.Requests.Last().GetProperty("params").GetProperty("expectedTurnId").GetString() == TurnId);
        Check(active.Requests.Last().GetProperty("params").GetProperty("input")[0]
            .GetProperty("text").GetString() == "[via Voice Connector]\nУточни");

        var fallback = await RunScriptAsync("fallback", Hello(), Loaded(ThreadId), Resumed(),
            Metadata("active"), new { id = 5, error = new { code = -32601, message = "Unsupported" } },
            new { id = 6, result = new { thread = new {
                id = ThreadId, turns = new[] { new { id = TurnId, status = "inProgress" } } } } },
            new { id = 7, result = new { turnId = TurnId } });
        Check(fallback.Result.Status == CodexSendStatus.Accepted);
        Check(Method(fallback.Requests.Last()) == "turn/steer");

        var wrongThread = await RunScriptAsync("Не отправлять", Hello(), Loaded(OtherThreadId));
        Check(wrongThread.Result.Status == CodexSendStatus.Rejected);
        Check(wrongThread.Requests.All(request => Method(request) is not ("turn/start" or "turn/steer")));

        var rejected = await RunScriptAsync("Отказ", Hello(), Loaded(ThreadId), Resumed(),
            Metadata("idle"), new { id = 7, error = new { code = -32000, message = "Busy" } });
        Check(rejected.Result.Status == CodexSendStatus.Rejected);

        var unknown = await RunScriptAsync("Потерян ответ", Hello(), Loaded(ThreadId), Resumed(),
            Metadata("idle"));
        Check(unknown.Result.Status == CodexSendStatus.Unknown);
        Check(Method(unknown.Requests.Last()) == "turn/start");

        var invalid = await RunScriptAsync("Неверная сессия", Hello(), Array.Empty<object>(), "not-a-uuid");
        Check(invalid.Result.Status == CodexSendStatus.Rejected && invalid.Requests.Length == 0);
    }

    private static object Hello() => new { id = 1, result = new { userAgent = "test" } };
    private static object Loaded(string id) => new { id = 2, result = new { data = new[] { id } } };
    private static object Resumed() => new { id = 3, result = new { thread = new { id = ThreadId } } };
    private static object Metadata(string status) => new {
        id = 4, result = new { thread = new { id = ThreadId, source = "cli", status = new { type = status } } }
    };

    private static async Task<(CodexSendResult Result, JsonElement[] Requests)> RunScriptAsync(
        string message, object first, params object[] rest) =>
        await RunScriptAsync(message, first, rest, ThreadId);

    private static async Task<(CodexSendResult Result, JsonElement[] Requests)> RunScriptAsync(
        string message, object first, object[] rest, string threadId)
    {
        var responses = new[] { first }.Concat(rest)
            .Select(frame => JsonSerializer.Serialize(frame));
        using var fromServer = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", responses) + "\n"));
        using var toServer = new MemoryStream();
        var result = await CodexAppServerSender.SendOverProtocolAsync(
            toServer, fromServer, threadId, message);
        var requests = Encoding.UTF8.GetString(toServer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        return (result, requests);
    }

    private static async Task<(CodexConnectionResult Result, JsonElement[] Requests)> RunProbeScriptAsync(
        params object[] responses)
    {
        using var fromServer = new MemoryStream(Encoding.UTF8.GetBytes(
            responses.Length == 0 ? "" : string.Join("\n", responses.Select(frame => JsonSerializer.Serialize(frame))) + "\n"));
        using var toServer = new MemoryStream();
        var result = await CodexAppServerSender.ProbeOverProtocolAsync(toServer, fromServer);
        var requests = Encoding.UTF8.GetString(toServer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        return (result, requests);
    }

    private static string? Method(JsonElement request) => request.GetProperty("method").GetString();

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Codex app-server sender self-test failed.");
    }
}
