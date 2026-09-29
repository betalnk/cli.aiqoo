namespace CodexVoice;

/// <summary>Uses only a direct connection shared with the target Codex TUI.</summary>
internal static class CodexDirectSender
{
    internal static async Task<CodexConnectionResult> ProbeConnectionAsync(
        CancellationToken token = default)
    {
        try
        {
            var webSocket = CodexWebSocketConfig.FromEnvironment();
            return webSocket is null
                ? await CodexDaemonSender.ProbeConnectionAsync(token)
                : await CodexWebSocketSender.ProbeConnectionAsync(webSocket, token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(CodexConnectionStatus.Unavailable,
                $"Настройка прямого соединения неверна: {exception.Message}");
        }
    }

    internal static async Task<CodexSendResult> SendAsync(
        string threadId, string message, CancellationToken token = default)
    {
        try
        {
            if (!new SessionBindingStore().TryGetConfirmed(threadId, out var binding)
                || binding is null || binding.Source == "bootstrap")
                return new(CodexSendStatus.Rejected,
                    "Плагин Codex Voice ещё не подтвердил эту сессию. Текст сохранён для правки.");
            var webSocket = CodexWebSocketConfig.FromEnvironment();
            return webSocket is null
                ? await CodexDaemonSender.SendAsync(threadId, message, token)
                : await CodexWebSocketSender.SendAsync(webSocket, threadId, message, token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(CodexSendStatus.Rejected,
                $"Настройка прямого соединения неверна: {exception.Message}");
        }
    }
}
