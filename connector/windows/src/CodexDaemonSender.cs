namespace CodexVoice;

/// <summary>Uses the existing shared Codex daemon; it does not start or resume sessions.</summary>
internal static class CodexDaemonSender
{
    internal static async Task<CodexConnectionResult> ProbeConnectionAsync(
        CancellationToken token = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var socket = await CodexDaemonSocketTransport.ConnectAsync(timeout.Token);
            return await CodexWebSocketSender.ProbeOverProtocolAsync(socket, timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(CodexConnectionStatus.Unavailable,
                "Общий локальный процесс Codex недоступен. Новую сессию нужно запускать с daemon_auto_start.");
        }
    }

    internal static async Task<CodexSendResult> SendAsync(
        string threadId, string message, CancellationToken token = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var socket = await CodexDaemonSocketTransport.ConnectAsync(timeout.Token);
            return await CodexWebSocketSender.SendOverProtocolAsync(
                socket, threadId, message, timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(CodexSendStatus.Rejected,
                "Нет прямого соединения с общим локальным процессом Codex. Текст не отправлен.");
        }
    }
}
