namespace CodexVoice;

internal static class ForegroundTerminalSenderSelfTests
{
    internal static void Run()
    {
        var longDictation = new string('а', 5_000);
        var prepared = ForegroundTerminalSender.PrepareText(longDictation);
        Check(prepared == "[via Voice Connector] " + longDictation,
            "A dictation longer than the former 4096-character limit must remain one message.");
        Check(ForegroundTerminalSender.PrepareText(new string('а', 100_000)) is not null,
            "The sender must accept the largest text the draft store can recover.");
        Check(ForegroundTerminalSender.PrepareText(new string('а', 100_001)) is null,
            "Oversized text must be rejected before any key is inserted.");
        Check(ForegroundTerminalSender.PrepareText("первая строка\nвторая") is null,
            "A newline must never reach the terminal as an executable second line.");
        Check(ForegroundTerminalSender.PrepareText("текст && команда") is null,
            "Shell operators must remain blocked if Codex exits during input.");
        Check(ForegroundTerminalSender.PasteSettleDelay(5_000)
              > ForegroundTerminalSender.PasteSettleDelay(20)
              && ForegroundTerminalSender.PasteSettleDelay(20).TotalMilliseconds > 120,
            "Enter must be sent after Codex's paste-burst Enter suppression window.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
