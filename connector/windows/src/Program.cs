using Avalonia;
using System.Text;

namespace CodexVoice;

internal static class Program
{
    internal static bool Preview { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test-hook-stdin")
        {
            try { return ReadHookInput() == "codex-voice-stdin-probe" ? 0 : 8; }
            catch { return 9; }
        }
        if (args.Contains("--register-from-hook", StringComparer.OrdinalIgnoreCase))
        {
            if (args.Length != 1) return 2;
            string payload;
            try { payload = ReadHookInput(); }
            catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException or IOException)
            {
                Console.Error.WriteLine("HOOK_REGISTRATION_FAILED: " + exception.Message);
                return 5;
            }
            if (!new SessionBindingStore().TryRegisterHookPayload(payload, out var error))
            {
                Console.Error.WriteLine("HOOK_REGISTRATION_FAILED: " + error);
                return 5;
            }
            // Hook stdout becomes Codex developer context; successful registration is silent.
            return 0;
        }
        if (args.Contains("--bind-session", StringComparer.OrdinalIgnoreCase))
        {
            if (args.Length != 1) return 2;
            var bindings = new SessionBindingStore();
            if (!bindings.TryBind(Environment.GetEnvironmentVariable("CODEX_THREAD_ID"),
                    out var bound, out var error) || bound is null)
            {
                Console.Error.WriteLine("BIND_FAILED: " + error);
                return 5;
            }
            Console.WriteLine($"MANUAL_RECORD_ONLY: {bound.Name} | {bound.Project} [{bound.ThreadId}]");
            return 0;
        }
        if (args.Contains("--binding-status", StringComparer.OrdinalIgnoreCase))
        {
            if (args.Length != 1) return 2;
            var threadId = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            if (!SessionResolver.TryCanonicalThreadId(threadId, out var canonical))
            {
                Console.Error.WriteLine("BINDING_UNAVAILABLE: CODEX_THREAD_ID is missing or invalid.");
                return 4;
            }
            if (!new SessionBindingStore().TryGetConfirmed(canonical, out var bound) || bound is null)
            {
                Console.WriteLine("NOT_BOUND: current Codex thread has no fresh confirmed binding.");
                return 3;
            }
            Console.WriteLine($"BOUND: {bound.Name} | {bound.Project} [{bound.ThreadId}]");
            return 0;
        }
        if (args.Contains("--self-test-bindings", StringComparer.OrdinalIgnoreCase))
            return args.Length == 1 ? SessionBindingSelfTests.Run() : 2;
        if (args.Contains("--self-test-quick-hotkey", StringComparer.OrdinalIgnoreCase))
            return args.Length == 1 ? QuickHotkeySelfTests.Run() : 2;
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return SelfTests.Run();
        if (args.Contains("--probe-foreground", StringComparer.OrdinalIgnoreCase))
        {
            var ok = SessionResolver.TryCapture(out _, out var error);
            Console.WriteLine(ok ? "PROBE_OK" : $"PROBE_FAILED: {error}");
            return ok ? 0 : 3;
        }
        if (args.Length == 1 && args[0].Equals("--probe-open-sessions", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"OPEN_SESSIONS: {SessionResolver.ListOpenSessions().Count}");
            return 0;
        }
        if (args.Contains("--probe-codex", StringComparer.OrdinalIgnoreCase))
        {
            var found = CodexQueueSender.FindExecutable() is not null;
            Console.WriteLine(found ? "CODEX_OK" : "CODEX_NOT_FOUND");
            return found ? 0 : 4;
        }
        if (args.Contains("--probe-connection", StringComparer.OrdinalIgnoreCase))
        {
            if (args.Length != 1) return 2;
            var connection = CodexDirectSender.ProbeConnectionAsync().GetAwaiter().GetResult();
            Console.WriteLine(connection.Status == CodexConnectionStatus.Connected
                ? "DIRECT_CONNECTED: " + connection.Message
                : "DIRECT_UNAVAILABLE: " + connection.Message);
            return connection.Status == CodexConnectionStatus.Connected ? 0 : 6;
        }
        if (args.Length == 2 && args[0].Equals("--pending-queue", StringComparison.OrdinalIgnoreCase))
        {
            if (!SessionResolver.TryCountQueuedItems(args[1], out var count, out var error))
            {
                Console.Error.WriteLine("QUEUE_CHECK_FAILED: " + error);
                return 6;
            }
            Console.WriteLine("PENDING_QUEUE_COUNT: " + count);
            return count == 0 ? 0 : 7;
        }

        Preview = args.Contains("--preview", StringComparer.OrdinalIgnoreCase);

        var root = Preview ? null : FindModelsRoot(args);
        if (!Preview && root is null)
        {
            Console.Error.WriteLine("Не найдена локальная модель Vosk. Укажите --models-root <каталог моделей Codex Voice>.");
            return 2;
        }

        if (root is not null) Directory.SetCurrentDirectory(root);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    private static string? FindModelsRoot(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--models-root", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (index + 1 >= args.Length) return null;
            var selected = Path.GetFullPath(args[index + 1]);
            return HasModel(selected) ? selected : null;
        }

        var configured = Environment.GetEnvironmentVariable("CODEX_VOICE_MODELS_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && HasModel(configured))
            return Path.GetFullPath(configured);

        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            for (var depth = 0; dir is not null && depth < 9; dir = dir.Parent, depth++)
                if (HasModel(dir.FullName)) return dir.FullName;
        }
        return null;
    }

    private static bool HasModel(string path) =>
        Directory.Exists(Path.Combine(path, "vosk-model-small-ru-0.22"));

    private static string ReadHookInput()
    {
        const int maxBytes = 1024 * 1024;
        using var stream = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("Hook payload is too large.");
            buffer.Write(chunk, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
