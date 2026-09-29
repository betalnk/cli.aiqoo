namespace CodexVoice;

internal static class SelfTests
{
    public static int Run()
    {
        try
        {
            Check(SessionResolver.TryParseTitle("⠦ Работа над задачей | demo", out var name, out var project));
            Check(name == "Работа над задачей" && project == "demo");
            Check(SessionResolver.TryParseTitle("[ . ] Action Required | Работа над задачей | demo", out name, out project));
            Check(name == "Работа над задачей" && project == "demo");
            Check(!SessionResolver.TryParseTitle("Windows PowerShell", out _, out _));

            var threads = new (string Id, string Name, string Cwd)[]
            {
                ("thread-demo", "Работа над задачей", @"C:\work\demo"),
                ("thread-other", "Работа над задачей", @"C:\work\other")
            };
            Check(SessionResolver.TryResolveTitle("Action Required | Работа над задачей | demo",
                threads, out var target) && target.Id == "thread-demo");
            Check(!SessionResolver.TryResolveTitle("Работа над задачей | missing", threads, out _));
            Check(!SessionResolver.TryResolveTitle("Работа над задачей | demo",
                threads.Append(("thread-duplicate", "Работа над задачей", @"D:\other\demo")), out _));

            var buffer = new TranscriptBuffer();
            buffer.SetPartial(" первая  фраза ");
            Check(buffer.Preview == "первая фраза" && buffer.Final.Length == 0);
            buffer.Add(2, " второй сегмент ");
            buffer.Add(1, "первый сегмент");
            buffer.Refine(2, "уточнённый сегмент");
            buffer.Refine(1, ""); // Empty Whisper fallback must preserve the Vosk draft.
            Check(buffer.Final == "первый сегмент уточнённый сегмент");
            buffer.SetPartial("в процессе");
            Check(buffer.Final == "первый сегмент уточнённый сегмент");
            Check(buffer.Preview.EndsWith("в процессе", StringComparison.Ordinal));
            buffer.Clear();
            Check(buffer.Preview.Length == 0);

            const string payload = "текст && rm -rf / \"quote\" `whoami`";
            var marked = CodexQueueSender.ConnectorPrefix + "\n" + payload;
            Check(CodexQueueSender.WithConnectorPrefix(marked) == marked);
            Check(CodexQueueSender.WithConnectorPrefix(payload) == marked);
            CodexAppServerSenderSelfTests.RunAsync().GetAwaiter().GetResult();
            CodexWebSocketSenderSelfTests.RunAsync().GetAwaiter().GetResult();
            CodexDaemonSocketTransportSelfTests.RunAsync().GetAwaiter().GetResult();
            CodexTerminalTitleIdentitySelfTests.Run();
            CodexLoadedThreadResolverSelfTests.RunAsync().GetAwaiter().GetResult();
            CodexHookTrustProbeSelfTests.RunAsync().GetAwaiter().GetResult();
            CodexRolloutReceiptVerifierSelfTests.RunAsync().GetAwaiter().GetResult();
            VoiceDraftStoreSelfTests.Run();
            ForegroundTerminalSenderSelfTests.Run();
            VoicePairingCryptoSelfTests.Run();
            VoicePairingRecoverySelfTests.Run();
            CliHistoryCryptoSelfTests.Run();
            CliCommandSelfTests.RunAsync().GetAwaiter().GetResult();

            var longAnswer = string.Concat(Enumerable.Repeat("Проверка ответа с русским текстом. ", 750)) + "конец 🎙️";
            var speechChunks = SessionPlaybackController.SplitText(longAnswer);
            Check(speechChunks.Count > 1);
            Check(speechChunks.All(chunk => chunk.Length <= LocalSpeechPlayer.MaxTextLength));
            Check(string.Concat(speechChunks) == longAnswer);
            VersionCheckSelfTests.RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("SELF_TEST_OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SELF_TEST_FAILED: {exception}");
            return 1;
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Self-test assertion failed.");
    }
}
