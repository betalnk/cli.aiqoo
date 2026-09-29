namespace CodexVoice;

internal static class CodexTerminalTitleIdentitySelfTests
{
    internal static void Run()
    {
        const string first = "01234567-89ab-4cde-8f01-12345abcdef0";
        const string samePrefix = "01234567-89ab-4cde-8f01-12345fedcba9";
        const string other = "fedcba98-7654-4321-8abc-def012345678";
        var displayed = first[..29] + "...";

        Check(Resolve(displayed, [first], out var selected) && selected == first);
        Check(Resolve("codex | " + displayed + " | project", [other, first], out selected) &&
            selected == first);
        Check(Resolve("⠋ " + displayed + " | project", [first], out selected) && selected == first);
        Check(Resolve("● ⠋ " + displayed, [first], out selected) && selected == first);
        Check(Resolve("[ ! ] Action Required | " + displayed, [first], out selected) &&
            selected == first);

        Check(!Resolve(displayed, [], out _));
        Check(!Resolve(displayed, [other], out _));
        Check(!Resolve(displayed, [first, first], out _));
        Check(!Resolve(displayed, [first, samePrefix], out _));
        Check(!Resolve(displayed + " | " + other[..29] + "...", [first], out _));
        Check(!Resolve(null, [first], out _));
        Check(!Resolve("project | no thread id", [first], out _));
        Check(!Resolve(first, [first], out _));
        Check(!Resolve("Task " + displayed, [first], out _));
        Check(!Resolve("Windows PowerShell - " + displayed, [first], out _));
        Check(!Resolve("project|" + displayed, [first], out _));
        Check(!Resolve("project |  | " + displayed, [first], out _));
        Check(!Resolve(displayed.ToUpperInvariant(), [first], out _));
        Check(!Resolve(displayed[..^3] + "…", [first], out _));
        Check(!Resolve(displayed[..^4] + "...", [first], out _));
        Check(!Resolve(displayed.Replace('-', '_'), [first], out _));
        Check(!Resolve(displayed + " extra", [first], out _));
        Check(!Resolve(displayed, [first.ToUpperInvariant()], out _));
        Check(!Resolve(displayed, ["not-a-uuid"], out _));

        Check(CodexTerminalTitleIdentity.TryResolveStandalone(displayed, [first], out selected)
            && selected == first);
        Check(CodexTerminalTitleIdentity.TryResolveStandalone("⠋ " + displayed, [first], out selected)
            && selected == first);
        Check(!CodexTerminalTitleIdentity.TryResolveStandalone("project | " + displayed,
            [first], out _));
        Check(CodexTerminalTitleIdentity.TryReadStandaloneDisplayedId("⠋ " + displayed,
            out var extracted) && extracted == displayed);
        Check(!CodexTerminalTitleIdentity.TryReadStandaloneDisplayedId("project | " + displayed,
            out _));
    }

    private static bool Resolve(string? title, IEnumerable<string> ids, out string id) =>
        CodexTerminalTitleIdentity.TryResolve(title, ids, out id);

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Terminal title identity self-test failed.");
    }
}
