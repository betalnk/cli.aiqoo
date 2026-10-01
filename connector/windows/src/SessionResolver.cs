using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexVoice;

internal sealed record CapturedSession(
    IntPtr Window, string WindowTitle, string Name, string Project, string ThreadId,
    uint WindowProcessId = 0, long WindowProcessStartTicks = 0);
internal sealed record QuickTarget(IntPtr Window, uint WindowProcessId, string WindowTitle, CapturedSession Session);
internal sealed record SessionIdentity(string ThreadId, string Name, string Cwd)
{
    internal string Project => Path.GetFileName(Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

internal static partial class SessionResolver
{
    private const uint SqliteReadOnly = 0x00000001;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "WindowsTerminal", "conhost", "OpenConsole"
    };

    public static bool TryCapture(out CapturedSession? session, out string error)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            session = null;
            error = "Не найдено активное окно.";
            return false;
        }
        return TryCaptureWindow(window, out session, out error);
    }

    /// <summary>Capture the terminal that owned the foreground when the hotkey was pressed.</summary>
    internal static bool TryCaptureQuickTarget(IntPtr window, out QuickTarget? target, out string error)
    {
        target = null;
        if (!TryCaptureWindow(window, out var session, out error) || session is null)
            return false;
        target = new QuickTarget(window, session.WindowProcessId, session.WindowTitle, session);
        return true;
    }

    /// <summary>Reject a changed foreground window or a title that no longer maps uniquely.</summary>
    internal static bool TryValidateQuickTarget(QuickTarget target,
        out string error, bool requireForeground = true)
    {
        if (target.Window != target.Session.Window
            || target.WindowProcessId != target.Session.WindowProcessId
            || !string.Equals(target.WindowTitle, target.Session.WindowTitle, StringComparison.Ordinal)
            || (requireForeground && GetForegroundWindow() != target.Window))
        {
            error = "Передняя вкладка изменилась во время записи. Текст не отправлен.";
            return false;
        }
        return TryValidate(target.Session, out error);
    }

    public static IReadOnlyList<CapturedSession> ListOpenSessions()
    {
        return TryReadOpenSessions(out var sessions) ? sessions : Array.Empty<CapturedSession>();
    }

    public static IReadOnlyList<CapturedSession> ListOpenSessions(CapturedSession captured)
    {
        var result = ListOpenSessions().ToList();
        var index = result.FindIndex(item => item.Window == captured.Window
            && item.ThreadId == captured.ThreadId
            && item.WindowProcessId == captured.WindowProcessId
            && item.WindowProcessStartTicks == captured.WindowProcessStartTicks);
        if (index > 0)
        {
            var selected = result[index];
            result.RemoveAt(index);
            result.Insert(0, selected);
        }
        return result;
    }

    public static bool TryValidate(CapturedSession selected, out string error)
    {
        if (!TryGetTerminalProcessIdentity(selected.Window, out var processId, out var startTicks)
            || processId != selected.WindowProcessId
            || startTicks != selected.WindowProcessStartTicks)
        {
            error = "Окно терминала закрылось или сменило процесс. Текст не отправлен.";
            return false;
        }

        if (!TryReadOpenSessions(out var sessions))
        {
            error = "Не удалось перечитать локальную базу сессий Codex. Текст не отправлен.";
            return false;
        }

        var current = sessions.SingleOrDefault(item => item.Window == selected.Window);
        if (current is null
            || current.ThreadId != selected.ThreadId
            || !current.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase)
            || !current.Project.Equals(selected.Project, StringComparison.OrdinalIgnoreCase)
            || current.WindowProcessId != selected.WindowProcessId
            || current.WindowProcessStartTicks != selected.WindowProcessStartTicks)
        {
            error = "Заголовок вкладки больше не соответствует выбранной сессии Codex. Текст не отправлен.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryCaptureWindow(IntPtr window, out CapturedSession? session, out string error)
    {
        session = null;
        if (!TryGetTerminalProcessIdentity(window, out _, out _))
        {
            error = "Выберите видимое окно Codex в CMD, PowerShell или Windows Terminal.";
            return false;
        }
        if (!TryReadOpenSessions(out var sessions))
        {
            error = "Не удалось прочитать локальную базу сессий Codex.";
            return false;
        }

        session = sessions.SingleOrDefault(item => item.Window == window);
        if (session is null)
        {
            error = "Заголовок окна не удалось однозначно сопоставить с сессией Codex.";
            return false;
        }
        error = "";
        return true;
    }

    private static bool TryReadOpenSessions(out IReadOnlyList<CapturedSession> sessions)
    {
        sessions = Array.Empty<CapturedSession>();
        var database = DatabasePath();
        if (!File.Exists(database)) return false;

        IReadOnlyList<(string Id, string Name, string Cwd)> threads;
        try { threads = ReadThreads(database); }
        catch { return false; }

        var candidates = new List<CapturedSession>();
        EnumWindows((window, _) =>
        {
            try
            {
                if (!TryGetTerminalProcessIdentity(window, out var processId, out var startTicks))
                    return true;
                var title = ReadWindowTitle(window);
                if (!TryResolveTitle(title, threads, out var target)
                    || !TryCanonicalThreadId(target.Id, out var canonical))
                    return true;
                candidates.Add(new CapturedSession(window, title, target.Name, target.Project,
                    canonical, processId, startTicks));
            }
            catch { /* A disappearing terminal is not a selectable target. */ }
            return true;
        }, IntPtr.Zero);

        // One UUID appearing in several visible windows cannot identify an exact destination.
        sessions = candidates.GroupBy(item => item.ThreadId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
        return true;
    }

    internal static bool TryParseTitle(string title, out string name, out string project)
    {
        name = project = "";
        if (string.IsNullOrWhiteSpace(title)) return false;
        // Codex's activity spinner uses Braille characters at the start of a console title.
        var clean = SpinnerPrefix().Replace(title.Trim(), "").Trim();
        // Codex can prepend transient state, e.g. "Action Required | name | project".
        // The rightmost pair still identifies the visible session.
        var parts = clean.Split(" | ", StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        name = parts[^2];
        project = parts[^1];
        return name.Length > 0 && project.Length > 0;
    }

    internal static bool TryResolveTitle(string title,
        IEnumerable<(string Id, string Name, string Cwd)> threads,
        out (string Id, string Name, string Project) target)
    {
        target = default;
        if (!TryParseTitle(title, out var name, out var project)) return false;
        var matches = MatchingThreads(threads, name, project).Take(2).ToArray();
        if (matches.Length != 1) return false;
        target = (matches[0].Id, name, project);
        return true;
    }

    private static string DatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");

    internal static bool TryCanonicalThreadId(string? value, out string canonical)
    {
        canonical = "";
        if (value is null || value.Length != 36 ||
            !Guid.TryParseExact(value, "D", out var parsed)) return false;
        canonical = parsed.ToString("D");
        return string.Equals(value, canonical, StringComparison.Ordinal);
    }

    /// <summary>Find the local rollout for an exact CLI session ID without reading its contents.</summary>
    internal static bool TryGetRolloutPath(string threadId, out string path)
        => TryGetRolloutPath(DatabasePath(), threadId, out path);

    internal static bool TryGetRolloutPath(string file, string threadId, out string path)
    {
        path = "";
        if (!TryCanonicalThreadId(threadId, out var canonical)) return false;
        if (!File.Exists(file)) return false;

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            var rc = sqlite3_open_v2(file, out database, SqliteReadOnly, IntPtr.Zero);
            if (rc != 0 || database == IntPtr.Zero) return false;
            // The canonical value contains only UUID hex digits and hyphens.
            // The selected UUID remains the identity after a thread is resumed in CLI.
            var sql = $"SELECT rollout_path FROM threads WHERE id = '{canonical}' AND archived = 0";
            rc = sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero);
            if (rc != 0 || statement == IntPtr.Zero) return false;
            if (sqlite3_step(statement) != SqliteRow) return false;
            var candidate = Column(statement, 0);
            if (sqlite3_step(statement) != SqliteDone
                || !Path.IsPathFullyQualified(candidate)
                || !candidate.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate)) return false;
            path = Path.GetFullPath(candidate);
            return true;
        }
        catch { return false; }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (database != IntPtr.Zero) sqlite3_close(database);
        }
    }

    /// <summary>Read-only migration guard; never exposes queued message content.</summary>
    internal static bool TryCountQueuedItems(string? threadId, out int count, out string error)
    {
        count = 0;
        if (!TryCanonicalThreadId(threadId, out var canonical))
        {
            error = "Неверный UUID сессии Codex.";
            return false;
        }
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "queue_1.sqlite");
        if (!File.Exists(file))
        {
            error = "Локальная очередь Codex недоступна.";
            return false;
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            var rc = sqlite3_open_v2(file, out database, SqliteReadOnly, IntPtr.Zero);
            if (rc != 0 || database == IntPtr.Zero)
                throw new InvalidOperationException($"SQLite open: {rc}");
            // canonical contains only lowercase UUID hex digits and hyphens.
            var sql = $"SELECT COUNT(*) FROM queued_items WHERE thread_id = '{canonical}'";
            rc = sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero);
            if (rc != 0 || statement == IntPtr.Zero)
                throw new InvalidOperationException($"SQLite query: {rc}");
            rc = sqlite3_step(statement);
            if (rc != SqliteRow || !int.TryParse(Column(statement, 0), out count) || count < 0)
                throw new InvalidOperationException($"SQLite read: {rc}");
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            count = 0;
            error = $"Не удалось проверить очередь Codex: {exception.Message}";
            return false;
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (database != IntPtr.Zero) sqlite3_close(database);
        }
    }

    /// <summary>Resolve a UUID supplied by the invoking Codex process, never by window title.</summary>
    internal static bool TryResolveExactThread(string threadId, out SessionIdentity? identity, out string error)
    {
        identity = null;
        if (!TryCanonicalThreadId(threadId, out var canonical))
        {
            error = "CODEX_THREAD_ID отсутствует или имеет неверный формат UUID.";
            return false;
        }

        var database = DatabasePath();
        if (!File.Exists(database))
        {
            error = "Не найдена локальная база сессий Codex.";
            return false;
        }

        try
        {
            var matches = ReadThreads(database)
                .Where(row => string.Equals(row.Id, canonical, StringComparison.Ordinal))
                .Take(2).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Name) ||
                string.IsNullOrWhiteSpace(matches[0].Cwd))
            {
                error = "Точная сессия CODEX_THREAD_ID не найдена в Codex.";
                return false;
            }

            identity = new SessionIdentity(canonical, matches[0].Name, matches[0].Cwd);
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            error = $"Не удалось проверить точную сессию Codex: {exception.Message}";
            return false;
        }
    }

    private static IEnumerable<(string Id, string Name, string Cwd)> MatchingThreads(
        IEnumerable<(string Id, string Name, string Cwd)> threads, string name, string project) =>
        threads.Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(t.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals(project, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetTerminalProcessIdentity(IntPtr window,
        out uint processId, out long startTicks)
    {
        processId = 0;
        startTicks = 0;
        if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window))
            return false;
        GetWindowThreadProcessId(window, out processId);
        if (processId == 0 || processId > int.MaxValue) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!TerminalProcesses.Contains(process.ProcessName)) return false;
            startTicks = process.StartTime.ToUniversalTime().Ticks;
            return startTicks > 0;
        }
        catch { return false; }
    }

    private static string ReadWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        var buffer = new StringBuilder(Math.Max(length + 1, 256));
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static IReadOnlyList<(string Id, string Name, string Cwd)> ReadThreads(string file)
    {
        var result = new List<(string, string, string)>();
        var rc = sqlite3_open_v2(file, out var db, SqliteReadOnly, IntPtr.Zero);
        if (rc != 0 || db == IntPtr.Zero) throw new InvalidOperationException($"SQLite open: {rc}");
        try
        {
            // Source records where a thread was created, not the host of a resumed TUI.
            // Foreground routing still requires a visible terminal and a unique name/project.
            const string sql = "SELECT id, name, cwd FROM threads WHERE archived = 0 AND name IS NOT NULL";
            rc = sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero);
            if (rc != 0) throw new InvalidOperationException($"SQLite query: {rc}");
            try
            {
                while ((rc = sqlite3_step(statement)) == SqliteRow)
                    result.Add((Column(statement, 0), Column(statement, 1), Column(statement, 2)));
                if (rc != SqliteDone) throw new InvalidOperationException($"SQLite read: {rc}");
            }
            finally { sqlite3_finalize(statement); }
        }
        finally { sqlite3_close(db); }
        return result;
    }

    private static string Column(IntPtr statement, int index)
    {
        var pointer = sqlite3_column_text(statement, index);
        var bytes = sqlite3_column_bytes(statement, index);
        if (pointer == IntPtr.Zero || bytes <= 0) return "";
        var buffer = new byte[bytes];
        Marshal.Copy(pointer, buffer, 0, bytes);
        return Encoding.UTF8.GetString(buffer);
    }

    [GeneratedRegex("^[\\u2800-\\u28ff\\s]+")]
    private static partial Regex SpinnerPrefix();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr database, uint flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
}
