namespace CodexVoice;

internal delegate bool QuickTargetVerifier(QuickTarget target,
    out string error, bool requireForeground);

/// <summary>Checks the captured foreground Codex session before quick send.</summary>
internal static class QuickSendGate
{
    internal static bool TryAuthorize(QuickTarget? target, string? text, bool automatic,
        out string error, QuickTargetVerifier? verifier = null)
    {
        if (target is null)
        {
            error = "Получатель не определён. Выберите открытую вкладку Codex и повторите запись.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Введите текст перед отправкой.";
            return false;
        }

        // The release gesture may send automatically only while its source tab remains foreground.
        // An explicit click starts in this editor; the sender will restore and recheck the tab.
        return (verifier ?? SessionResolver.TryValidateQuickTarget)(
            target, out error, requireForeground: automatic);
    }
}
