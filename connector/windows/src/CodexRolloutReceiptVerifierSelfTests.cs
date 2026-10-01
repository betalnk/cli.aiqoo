using System.Text.Json;

namespace CodexVoice;

internal static class CodexRolloutReceiptVerifierSelfTests
{
    internal static async Task RunAsync()
    {
        var file = Path.GetTempFileName();
        try
        {
            const string message = "[via Voice Connector] проверка";
            File.WriteAllText(file, UserLine(message) + "\n");
            var baseline = new CodexReceiptBaseline(file, new FileInfo(file).Length);
            var receipt = CodexRolloutReceiptVerifier.WaitForUserMessageAsync(baseline, message);
            await Task.Delay(250);
            if (receipt.IsCompleted)
                throw new InvalidOperationException("An old identical message counted as a new receipt.");
            File.AppendAllText(file, UserLine("другое сообщение") + "\n");
            await Task.Delay(250);
            if (receipt.IsCompleted)
                throw new InvalidOperationException("A different user message counted as a receipt.");
            File.AppendAllText(file, UserLine(message) + "\n");
            if (!await receipt)
                throw new InvalidOperationException("A new matching user message was not detected.");

            File.WriteAllText(file, UserLine(QuestionReply(message)) + "\n");
            baseline = new CodexReceiptBaseline(file, new FileInfo(file).Length);
            receipt = CodexRolloutReceiptVerifier.WaitForUserMessageAsync(baseline, message);
            File.AppendAllText(file, UserLine(QuestionReply("другой ответ", message)) + "\n");
            File.AppendAllText(file, UserLine(QuestionReply(message), "assistant") + "\n");
            File.AppendAllText(file, UserLine("<send_user_message_question_reply>{broken}") + "\n");
            await Task.Delay(250);
            if (receipt.IsCompleted)
                throw new InvalidOperationException("An old reply, question metadata or assistant quote counted as receipt.");
            File.AppendAllText(file, UserLine(QuestionReply(message)) + "\n");
            if (!await receipt)
                throw new InvalidOperationException("A newly received exact question answer was not detected.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string QuestionReply(string answer, string question = "Что произошло?") =>
        "<send_user_message_question_reply>\n" + JsonSerializer.Serialize(new[]
        {
            new { answer, question, questionItemId = "[\"request_user_input_async\",\"call_test\",0]" }
        }) + "\n</send_user_message_question_reply>";

    private static string UserLine(string text, string role = "user") => JsonSerializer.Serialize(new
    {
        type = "response_item",
        payload = new
        {
            type = "message",
            role,
            content = new[] { new { type = "input_text", text } }
        }
    });
}
