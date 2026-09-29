using System.Diagnostics;

namespace CodexVoice;

internal enum CodexQueueStatus { Queued, Rejected, Unknown }
internal sealed record CodexQueueResult(CodexQueueStatus Status, string Message);

internal static class CodexQueueSender
{
    internal const string ConnectorPrefix = "[via Voice Connector]";

    /// <summary>Only an explicit caller may queue to an independently confirmed thread binding.</summary>
    internal static async Task<CodexQueueResult> QueueAsync(
        BoundSession selected, string message, SessionBindingStore? bindings = null,
        CancellationToken token = default)
    {
        if (selected is null || !SessionResolver.TryCanonicalThreadId(selected.ThreadId, out _)
            || string.IsNullOrWhiteSpace(message))
            return new(CodexQueueStatus.Rejected, "Неверная сессия или пустое сообщение.");

        var executable = FindExecutable();
        if (executable is null)
            return new(CodexQueueStatus.Rejected, "Не найден Codex CLI.");

        // Recheck the exact environment-derived binding just before launching queue.
        // A window title and HWND cannot distinguish tabs in the same terminal window.
        try
        {
            if (!(bindings ?? new SessionBindingStore()).TryGetConfirmed(selected.ThreadId, out var current)
                || current is null || !MatchesBinding(selected, current))
                return new(CodexQueueStatus.Rejected,
                    "Привязка сессии изменилась. Подключите вкладку заново перед отправкой.");
        }
        catch (Exception)
        {
            return new(CodexQueueStatus.Rejected, "Не удалось проверить привязку сессии Codex.");
        }

        using var process = new Process { StartInfo = CreateQueueStartInfo(executable, selected.ThreadId, message) };
        var started = false;
        try
        {
            if (!process.Start())
                return new(CodexQueueStatus.Rejected, "Не удалось запустить Codex CLI.");
            started = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            _ = await output;
            var stderr = (await error).Trim();
            if (process.ExitCode == 0)
                return new(CodexQueueStatus.Queued,
                    "Codex поставил сообщение в очередь следующего хода. Доставка ещё не подтверждена.");
            // Exit code alone does not prove whether Codex persisted the item before failing.
            return new(CodexQueueStatus.Unknown,
                stderr.Length == 0
                    ? $"Codex завершил постановку в очередь с кодом {process.ExitCode}. Проверьте сессию перед повтором."
                    : $"Codex не подтвердил очередь: {stderr[..Math.Min(stderr.Length, 240)]}. Проверьте сессию перед повтором.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return started
                ? new(CodexQueueStatus.Unknown,
                    "Codex не подтвердил очередь. Сообщение могло попасть в неё — проверьте сессию перед повтором.")
                : new(CodexQueueStatus.Rejected, $"Не удалось запустить Codex: {exception.Message}");
        }
        finally
        {
            try { if (started && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* Only this queue command belongs to the explicit request. */ }
        }
    }

    internal static bool MatchesBinding(BoundSession selected, BoundSession current) =>
        selected.ThreadId == current.ThreadId && selected.Name == current.Name
        && selected.Cwd == current.Cwd && selected.BoundAtUtc == current.BoundAtUtc;

    internal static ProcessStartInfo CreateQueueStartInfo(string executable, string threadId, string message)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        start.ArgumentList.Add("queue");
        start.ArgumentList.Add("--thread");
        start.ArgumentList.Add(threadId);
        start.ArgumentList.Add("--message");
        start.ArgumentList.Add(WithConnectorPrefix(message));
        return start;
    }

    internal static string WithConnectorPrefix(string message)
    {
        if (message.StartsWith(ConnectorPrefix + "\n", StringComparison.Ordinal)
            || message.StartsWith(ConnectorPrefix + "\r\n", StringComparison.Ordinal))
            return message;
        return ConnectorPrefix + "\n" + message;
    }

    internal static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            if (Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(configured);
            var besideShim = NativeFromNpmBin(Path.GetDirectoryName(Path.GetFullPath(configured))!);
            if (besideShim is not null) return besideShim;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var location = folder.Trim('"');
                var candidate = Path.Combine(location, "codex.exe");
                if (File.Exists(candidate)) return candidate;
                var native = NativeFromNpmBin(location);
                if (native is not null) return native;
            }
            catch { }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return NativeFromNpmBin(Path.Combine(appData, "npm"));
    }

    private static string? NativeFromNpmBin(string npmBin)
    {
        var candidate = Path.Combine(npmBin, "node_modules", "@openai", "codex", "node_modules",
            "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        return File.Exists(candidate) ? candidate : null;
    }
}
