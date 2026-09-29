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
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string UserLine(string text) => JsonSerializer.Serialize(new
    {
        type = "response_item",
        payload = new
        {
            type = "message",
            role = "user",
            content = new[] { new { type = "input_text", text } }
        }
    });
}
