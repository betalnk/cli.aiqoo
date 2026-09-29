namespace CodexVoice;

/// <summary>
/// Matches Codex 0.156.1's truncated terminal-title thread-id item against exact IDs
/// already registered by the plugin. A title is display text, not proof of a live TUI.
/// </summary>
internal static class CodexTerminalTitleIdentity
{
    private const int PrefixLength = 29;
    private const int DisplayLength = PrefixLength + 3;
    private const string Separator = " | ";
    private const string SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    /// <summary>
    /// Resolves exactly one displayed thread-id item to exactly one registered full UUID.
    /// The caller must supply only plugin-confirmed IDs and separately verify the window.
    /// </summary>
    internal static bool TryResolve(string? terminalTitle, IEnumerable<string>? pluginRegisteredThreadIds,
        out string threadId)
    {
        threadId = "";
        if (string.IsNullOrEmpty(terminalTitle) || terminalTitle.Length > 240 ||
            pluginRegisteredThreadIds is null)
            return false;

        string? displayedPrefix = null;
        foreach (var segment in terminalTitle.Split(Separator, StringSplitOptions.None))
        {
            if (segment.Length == 0 || segment.Contains('|') || HasControlCharacter(segment)) return false;

            var item = StripActivity(segment);
            if (!IsDisplayedThreadId(item)) continue;
            if (displayedPrefix is not null) return false;
            displayedPrefix = item[..PrefixLength];
        }

        if (displayedPrefix is null) return false;

        string? match = null;
        foreach (var candidate in pluginRegisteredThreadIds)
        {
            if (!IsCanonicalUuid(candidate) ||
                !candidate.AsSpan(0, PrefixLength).SequenceEqual(displayedPrefix))
                continue;
            if (match is not null) return false;
            match = candidate;
        }

        if (match is null) return false;
        threadId = match;
        return true;
    }

    /// <summary>
    /// For automatic foreground routing, require the configured title to contain only
    /// Codex's thread-id item (plus its transient activity marker).
    /// </summary>
    internal static bool TryResolveStandalone(string? terminalTitle,
        IEnumerable<string>? pluginRegisteredThreadIds, out string threadId)
    {
        threadId = "";
        if (!TryReadStandaloneDisplayedId(terminalTitle, out var displayedId)
            || !TryResolve(displayedId, pluginRegisteredThreadIds, out var candidate))
            return false;
        threadId = candidate;
        return true;
    }

    internal static bool TryReadStandaloneDisplayedId(string? terminalTitle, out string displayedId)
    {
        displayedId = "";
        if (string.IsNullOrWhiteSpace(terminalTitle) || terminalTitle.Length > 240
            || terminalTitle.Contains(Separator, StringComparison.Ordinal)
            || terminalTitle.Contains('|') || HasControlCharacter(terminalTitle))
            return false;
        var item = StripActivity(terminalTitle);
        if (!IsDisplayedThreadId(item)) return false;
        displayedId = item;
        return true;
    }

    private static string StripActivity(string segment)
    {
        // Codex joins its activity item to adjacent title items with one space.
        // Only its exact spinner frames and microphone marker are recognized.
        if (segment.StartsWith("● ", StringComparison.Ordinal)) segment = segment[2..];
        if (segment.Length > 2 && IsSpinner(segment[0]) && segment[1] == ' ')
            segment = segment[2..];
        if (segment.Length > 2 && segment[^2] == ' ' && IsSpinner(segment[^1]))
            segment = segment[..^2];
        if (segment.EndsWith(" ●", StringComparison.Ordinal)) segment = segment[..^2];
        return segment;
    }

    private static bool IsDisplayedThreadId(string value)
    {
        if (value.Length != DisplayLength || !value.EndsWith("...", StringComparison.Ordinal))
            return false;
        for (var index = 0; index < PrefixLength; index++)
        {
            var character = value[index];
            if (index is 8 or 13 or 18 or 23)
            {
                if (character != '-') return false;
            }
            else if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static bool IsCanonicalUuid(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    private static bool IsSpinner(char character) => SpinnerFrames.Contains(character);

    private static bool HasControlCharacter(string value) => value.Any(char.IsControl);
}
