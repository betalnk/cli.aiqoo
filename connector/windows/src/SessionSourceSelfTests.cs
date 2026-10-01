using System.Runtime.InteropServices;

namespace CodexVoice;

internal static class SessionSourceSelfTests
{
    internal static void Run()
    {
        var file = Path.Combine(Path.GetTempPath(), $"codex-voice-sources-{Guid.NewGuid():N}.sqlite");
        try
        {
            var rc = sqlite3_open_v2(file, out var database, 0x00000002 | 0x00000004, IntPtr.Zero);
            if (rc != 0 || database == IntPtr.Zero)
                throw new InvalidOperationException($"Source fixture open: {rc}");
            try
            {
                const string seed = """
                    CREATE TABLE threads (id TEXT, name TEXT, cwd TEXT, source TEXT, archived INTEGER);
                    INSERT INTO threads VALUES
                      ('cli-thread', 'CLI task', 'C:\work\demo', 'cli', 0),
                      ('vscode-thread', 'Voice task', 'C:\work\demo', 'vscode', 0),
                      ('exec-thread', 'Exec task', 'C:\work\demo', 'exec', 0),
                      ('archived-thread', 'Old task', 'C:\work\demo', 'vscode', 1),
                      ('unnamed-thread', NULL, 'C:\work\demo', 'vscode', 0),
                      ('duplicate-cli', 'Duplicate task', 'C:\work\demo', 'cli', 0),
                      ('duplicate-vscode', 'Duplicate task', 'D:\work\demo', 'vscode', 0);
                    """;
                rc = sqlite3_exec(database, seed, IntPtr.Zero, IntPtr.Zero, out var error);
                if (error != IntPtr.Zero) sqlite3_free(error);
                if (rc != 0) throw new InvalidOperationException($"Source fixture seed: {rc}");
            }
            finally { sqlite3_close(database); }

            var threads = SessionResolver.ReadThreads(file);
            Check(threads.Count == 5);
            Check(SessionResolver.TryResolveTitle("Voice task | demo", threads, out var voice)
                && voice.Id == "vscode-thread");
            Check(SessionResolver.TryResolveTitle("CLI task | demo", threads, out var cli)
                && cli.Id == "cli-thread");
            Check(SessionResolver.TryResolveTitle("Exec task | demo", threads, out var exec)
                && exec.Id == "exec-thread");
            Check(!SessionResolver.TryResolveTitle("Old task | demo", threads, out _));
            Check(!SessionResolver.TryResolveTitle("Duplicate task | demo", threads, out _));
            Check(!SessionResolver.TryResolveTitle("Voice task | other", threads, out _));
            CheckRolloutSources();
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    private static void CheckRolloutSources()
    {
        var file = Path.Combine(Path.GetTempPath(), $"codex-voice-rollout-sources-{Guid.NewGuid():N}.sqlite");
        var rollout = file + ".jsonl";
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        try
        {
            File.WriteAllText(rollout, "{}\n");
            var rc = sqlite3_open_v2(file, out var database, 0x00000002 | 0x00000004, IntPtr.Zero);
            if (rc != 0 || database == IntPtr.Zero)
                throw new InvalidOperationException($"Rollout source fixture open: {rc}");
            try
            {
                var quotedPath = rollout.Replace("'", "''");
                var seed = $"""
                    CREATE TABLE threads (id TEXT, rollout_path TEXT, source TEXT, archived INTEGER);
                    INSERT INTO threads VALUES
                      ('{ids[0]}', '{quotedPath}', 'cli', 0),
                      ('{ids[1]}', '{quotedPath}', 'vscode', 0),
                      ('{ids[2]}', '{quotedPath}', 'exec', 0),
                      ('{ids[3]}', '{quotedPath}', 'vscode', 1),
                      ('{ids[4]}', 'relative.jsonl', 'cli', 0);
                    """;
                rc = sqlite3_exec(database, seed, IntPtr.Zero, IntPtr.Zero, out var error);
                if (error != IntPtr.Zero) sqlite3_free(error);
                if (rc != 0) throw new InvalidOperationException($"Rollout source fixture seed: {rc}");
            }
            finally { sqlite3_close(database); }

            foreach (var id in ids.Take(3))
                Check(SessionResolver.TryGetRolloutPath(file, id, out var path) && path == rollout);
            Check(!SessionResolver.TryGetRolloutPath(file, ids[3], out _));
            Check(!SessionResolver.TryGetRolloutPath(file, ids[4], out _));
            Check(!SessionResolver.TryGetRolloutPath(file, Guid.NewGuid().ToString("D"), out _));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(rollout)) File.Delete(rollout);
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Session source regression failed.");
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        IntPtr callback, IntPtr argument, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr pointer);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);
}
