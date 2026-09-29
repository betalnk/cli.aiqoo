using Avalonia.Threading;

namespace CodexVoice;

/// <summary>Observes the exact Codex rollout after the voice layer has closed.</summary>
internal static class CodexDeliveryObserver
{
    internal static void Observe(CodexReceiptBaseline baseline, string submittedText,
        CapturedSession selected)
    {
        _ = ObserveAsync(baseline, submittedText, selected);
    }

    private static async Task ObserveAsync(CodexReceiptBaseline baseline,
        string submittedText, CapturedSession selected)
    {
        bool received;
        try
        {
            received = await Task.Run(() =>
                CodexRolloutReceiptVerifier.WaitForUserMessageAsync(baseline, submittedText));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            received = false;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var message = received
                ? $"Получено Codex → {selected.Project} / {selected.Name}"
                : "Enter передан Windows, но получение Codex не подтверждено. Проверьте вкладку перед повтором.";
            new StatusToast(message).Show();
        });
    }
}
