using Avalonia.Input;

namespace CodexVoice;

internal static class QuickHotkeySelfTests
{
    internal static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "codex-voice-quick-hotkey-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "quick-hotkey.json");
        try
        {
            var settings = new QuickHotkeySettings(path);
            Check(settings.Load(out var error) == HotkeyGesture.DefaultQuick && error.Length == 0,
                "missing settings must use the default");
            var custom = new HotkeyGesture(HotkeyGesture.Control | HotkeyGesture.Shift, 'Q');
            settings.Save(custom);
            Check(settings.Load(out error) == custom && error.Length == 0,
                "custom hotkey must survive a reload");
            Check(HotkeyGesture.TryFromKey(Key.V, KeyModifiers.Control | KeyModifiers.Alt,
                      out var captured) && captured == HotkeyGesture.DefaultQuick,
                "captured keys must map to Win32 modifiers and virtual key");
            Check(!HotkeyGesture.TryFromKey(Key.Space, KeyModifiers.Control | KeyModifiers.Alt, out _),
                "the overlay hotkey must be reserved");
            Check(!HotkeyGesture.TryFromKey(Key.V, KeyModifiers.Control, out _),
                "a single modifier must not become a global voice hotkey");
            File.WriteAllText(path, "{\"Schema\":1,\"Modifiers\":2,\"VirtualKey\":32}");
            Check(settings.Load(out error) == HotkeyGesture.DefaultQuick && error.Length > 0,
                "invalid persisted settings must fail safely");
            try
            {
                settings.Save(HotkeyGesture.Main);
                throw new InvalidOperationException("reserved hotkey was saved");
            }
            catch (ArgumentException) { }
            VerifySendGate();
            Console.WriteLine("QUICK_HOTKEY_TESTS_OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("QUICK_HOTKEY_TESTS_FAILED: " + exception.Message);
            return 1;
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static void VerifySendGate()
    {
        var session = new CapturedSession(IntPtr.Zero, "Test | test", "Test", "test",
            "00000000-0000-0000-0000-000000000001");
        var target = new QuickTarget(IntPtr.Zero, 0, "Test | test", session);
        var verificationCalls = 0;
        bool Verify(QuickTarget candidate, out string error, bool requireForeground)
        {
            verificationCalls++;
            error = requireForeground ? "foreground changed" : "";
            return !requireForeground;
        }
        Check(!QuickSendGate.TryAuthorize(null, "hello", true, out _,
                  Verify) && verificationCalls == 0,
            "no target must block terminal input before send");
        Check(!QuickSendGate.TryAuthorize(target, " ", true, out _,
                  Verify) && verificationCalls == 0,
            "empty text must block terminal input before send");
        Check(!QuickSendGate.TryAuthorize(target, "hello", true, out var error,
                  Verify) && error == "foreground changed" && verificationCalls == 1,
            "automatic send must require original foreground tab");
        Check(QuickSendGate.TryAuthorize(target, "hello", false, out error,
                  Verify) && error.Length == 0 && verificationCalls == 2,
            "explicit send may start from the editor after identity verification");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
