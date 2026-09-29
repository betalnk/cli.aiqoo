namespace CodexVoice;

internal sealed record CodexFirstTurnBootstrapResult(BoundSession? Binding, string Message)
{
    internal bool Success => Binding is not null;
}

/// <summary>
/// Gives an empty, already-loaded CLI thread a short-lived identity for its first voice turn.
/// The first turn's trusted plugin hook replaces this record with the durable hook binding.
/// </summary>
internal static class CodexFirstTurnBootstrap
{
    internal static async Task<CodexFirstTurnBootstrapResult> RegisterFromTitleAsync(
        string title, SessionBindingStore bindings, CancellationToken token = default)
    {
        if (!CodexTerminalTitleIdentity.TryReadStandaloneDisplayedId(title, out var displayedId))
            return new(null, "В заголовке передней вкладки нет точного ID новой сессии Codex.");
        var config = CodexWebSocketConfig.FromEnvironment();
        if (config is null)
            return new(null, "Для первого голосового сообщения нужен подключённый локальный Codex app-server.");

        var loaded = await CodexLoadedThreadResolver.ResolveAsync(config, displayedId, token);
        if (!loaded.Success || loaded.Identity is null)
            return new(null, loaded.Error);
        var trusted = await CodexHookTrustProbe.ProbeAsync(config, loaded.Identity.Cwd, token);
        if (!trusted.Trusted)
            return new(null, "Доверьте hooks Codex Voice через /hooks в Codex перед первым голосовым сообщением.");

        if (!bindings.TryRegisterBootstrap(loaded.Identity, out var error)
            || !bindings.TryGetConfirmed(loaded.Identity.ThreadId, out var binding)
            || binding is null)
            return new(null, string.IsNullOrWhiteSpace(error)
                ? "Не удалось привязать новую сессию Codex." : error);
        return new(binding, "");
    }

    internal static async Task<(bool Allowed, string Message)> RevalidateAsync(
        BoundSession binding, CodexWebSocketConfig config, CancellationToken token = default)
    {
        if (binding.Source != "bootstrap")
            return (false, "Временная привязка новой сессии недействительна.");
        var displayedId = binding.ThreadId[..29] + "...";
        var loaded = await CodexLoadedThreadResolver.ResolveAsync(config, displayedId, token);
        if (!loaded.Success || loaded.Identity is null
            || loaded.Identity.ThreadId != binding.ThreadId
            || !SameWindowsPath(loaded.Identity.Cwd, binding.Cwd))
            return (false, "Открытая сессия Codex изменилась. Текст сохранён для правки.");
        var trusted = await CodexHookTrustProbe.ProbeAsync(config, loaded.Identity.Cwd, token);
        if (!trusted.Trusted)
            return (false, "Hooks Codex Voice не доверены. Текст сохранён для правки.");
        return (true, "");
    }

    private static bool SameWindowsPath(string left, string right) =>
        Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
