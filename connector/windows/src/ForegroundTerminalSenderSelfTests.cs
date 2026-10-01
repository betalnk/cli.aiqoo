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
        Check(ForegroundTerminalSender.PrepareText("первая строка\r\nвторая")
              == "[via Voice Connector] первая строка\nвторая",
            "Ordinary multiline messages must remain one complete submitted message.");
        Check(ForegroundTerminalSender.PrepareText("пример: console.log(\"Привет 🙂\"); && $value")
              == "[via Voice Connector] пример: console.log(\"Привет 🙂\"); && $value",
            "Punctuation, code and emoji are literal message content, not shell syntax.");
        Check(ForegroundTerminalSender.PrepareText("разработчик 👩🏽‍💻 и семья 👨‍👩‍👧‍👦") is not null,
            "Supplementary Unicode and emoji joiners must remain valid text.");
        Check(ForegroundTerminalSender.PrepareText("код\n\treturn 1;")
              == "[via Voice Connector] код\n    return 1;", "Tabs keep indentation without invoking terminal completion.");
        Check(ForegroundTerminalSender.PrepareText("текст\0ещё") is null
              && ForegroundTerminalSender.PrepareText("текст\u001b[0m") is null
              && ForegroundTerminalSender.PrepareText("неполный \ud800") is null,
            "Terminal escape controls and malformed Unicode must not become keystrokes.");
        var unicodeBoundary = new string('а', 127) + "🙂" + "б";
        var firstChunk = ForegroundTerminalSender.TextChunkLength(unicodeBoundary, 0);
        Check(firstChunk == 127 && unicodeBoundary[..firstChunk] + unicodeBoundary[firstChunk..] == unicodeBoundary,
            "Keyboard pacing must not split a supplementary Unicode character.");
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
