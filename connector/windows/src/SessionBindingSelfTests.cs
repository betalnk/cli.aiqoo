namespace CodexVoice;

internal static class SessionBindingSelfTests
{
    internal static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-voice-bindings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");
        var bootstrapPath = Path.Combine(directory, "bootstrap.json");
        try
        {
            const string first = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
            const string second = "22222222-2222-4222-8222-222222222222";
            var known = new Dictionary<string, SessionIdentity>(StringComparer.Ordinal)
            {
                [first] = new(first, "Первая", @"C:\work\first"),
                [second] = new(second, "Вторая", @"C:\work\second")
            };
            var lookups = 0;
            bool Lookup(string id, out SessionIdentity? identity, out string error)
            {
                lookups++;
                var found = known.TryGetValue(id, out identity);
                error = found ? "" : "Exact ID not found";
                return found;
            }

            var now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");
            var store = new SessionBindingStore(path, Lookup, () => now);
            var bootstrapStore = new SessionBindingStore(bootstrapPath, Lookup, () => now);
            const string third = "33333333-3333-4333-8333-333333333333";
            Check(bootstrapStore.TryRegisterBootstrap(new LoadedThreadIdentity(third,
                @"C:\work\third", null), out _));
            Check(bootstrapStore.TryGetConfirmed(third, out var initial)
                && initial?.Source == "bootstrap" && initial.Name == "Новая сессия");
            now = now.AddMinutes(11);
            Check(!bootstrapStore.TryGetConfirmed(third, out _));
            now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");
            lookups = 0;
            Check(!store.TryBind(null, out _, out _));
            Check(!store.TryBind(first.ToUpperInvariant(), out _, out _));
            Check(!store.TryBind("Работа | first", out _, out _));
            Check(lookups == 0 && !File.Exists(path));

            Check(store.TryBind(first, out var bound, out _) && bound?.ThreadId == first);
            Check(File.Exists(path));
            Check(!store.TryGetConfirmed(first, out _)); // Manual recording cannot satisfy the plugin gate.
            Check(!store.TryRegisterHookPayload("{}", out _));
            Check(!store.TryRegisterHookPayload(HookPayload(first, @"C:\work\first", "Other"), out _));
            Check(store.TryRegisterHookPayload(HookPayload(first, @"C:\work\other"), out _));
            Check(!store.TryGetConfirmed(first, out _));
            Check(store.TryRegisterHookPayload(HookPayload(first, @"C:\work\first"), out _));
            Check(store.TryGetConfirmed(first, out bound) && bound?.Name == "Первая");
            Check(!store.TryGetConfirmed(second, out _));
            Check(store.TryBind(second, out _, out _));
            Check(!store.TryGetConfirmed(second, out _));
            Check(store.TryRegisterHookPayload(HookPayload(second, @"C:\work\second", "UserPromptSubmit"), out _));
            Check(store.ListConfirmed().Count == 2);
            Check(Directory.GetFiles(directory, "*.tmp").Length == 0);

            known.Remove(first);
            Check(store.ListConfirmed().Select(item => item.ThreadId).ToHashSet().SetEquals([first, second]));
            Check(store.TryGetConfirmed(first, out bound) && bound?.Name == "Новая сессия");
            now = now.AddHours(25);
            Check(store.ListConfirmed().Count == 0);
            Check(store.TryRegisterHookPayload(HookPayload(second, @"C:\work\second"), out _));
            Check(store.ListConfirmed().Count == 1);

            File.WriteAllText(path, "{bad");
            Check(!store.TryBind(second, out _, out var error) && error.Contains("повреждён"));
            Check(File.ReadAllText(path) == "{bad");
            Console.WriteLine("BINDING_SELF_TEST_OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("BINDING_SELF_TEST_FAILED: " + exception);
            return 1;
        }
        finally
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(bootstrapPath)) File.Delete(bootstrapPath);
                Directory.Delete(directory);
            }
            catch { /* A test cleanup failure must not touch any other path. */ }
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Session-binding self-test failed.");
    }

    private static string HookPayload(string threadId, string cwd, string eventName = "SessionStart") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            hook_event_name = eventName,
            session_id = threadId,
            cwd,
            source = "startup"
        });
}
