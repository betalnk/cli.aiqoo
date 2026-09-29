using System.Text.Json;
using Avalonia.Input;

namespace CodexVoice;

/// <summary>A deliberately narrow Windows global shortcut: two modifiers and one key.</summary>
internal readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    internal const uint Alt = 0x0001;
    internal const uint Control = 0x0002;
    internal const uint Shift = 0x0004;
    internal static readonly HotkeyGesture DefaultQuick = new(Control | Alt, 'V');
    internal static readonly HotkeyGesture Main = new(Control | Alt, 0x20);

    internal bool IsValid =>
        Modifiers is (Control | Alt) or (Control | Shift) or (Alt | Shift) or (Control | Alt | Shift)
        && (VirtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9' or >= 0x70 and <= 0x7B or 0x20)
        && this != Main;

    internal string Display =>
        $"{((Modifiers & Control) != 0 ? "Ctrl+" : "")}{((Modifiers & Alt) != 0 ? "Alt+" : "")}" +
        $"{((Modifiers & Shift) != 0 ? "Shift+" : "")}{KeyName(VirtualKey)}";

    internal static bool TryFromKey(Key key, KeyModifiers modifiers, out HotkeyGesture gesture)
    {
        var flags = (modifiers.HasFlag(KeyModifiers.Control) ? Control : 0u)
            | (modifiers.HasFlag(KeyModifiers.Alt) ? Alt : 0u)
            | (modifiers.HasFlag(KeyModifiers.Shift) ? Shift : 0u);
        var name = key.ToString();
        uint virtualKey = 0;
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z') virtualKey = name[0];
        else if (name.Length == 2 && name[0] == 'D' && name[1] is >= '0' and <= '9')
            virtualKey = name[1];
        else if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var number)
                 && number is >= 1 and <= 12) virtualKey = (uint)(0x6F + number);
        else if (key == Key.Space) virtualKey = 0x20;
        gesture = new HotkeyGesture(flags, virtualKey);
        return gesture.IsValid;
    }

    private static string KeyName(uint key) => key switch
    {
        0x20 => "Пробел",
        >= 0x70 and <= 0x7B => $"F{key - 0x6F}",
        _ => ((char)key).ToString()
    };
}

internal sealed class QuickHotkeySettings
{
    private const int MaxFileBytes = 4096;
    private readonly string _path;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexVoice", "quick-hotkey.json");

    internal QuickHotkeySettings(string? path = null) => _path = path ?? DefaultPath;

    internal HotkeyGesture Load(out string error)
    {
        error = "";
        if (!File.Exists(_path)) return HotkeyGesture.DefaultQuick;
        try
        {
            if (new FileInfo(_path).Length > MaxFileBytes)
                throw new InvalidDataException("Настройка слишком велика.");
            var file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllBytes(_path));
            var gesture = new HotkeyGesture(file?.Modifiers ?? 0, file?.VirtualKey ?? 0);
            if (file?.Schema != 1 || !gesture.IsValid)
                throw new InvalidDataException("Неверный формат клавиши.");
            return gesture;
        }
        catch
        {
            error = "Настройка быстрой клавиши повреждена. Используется Ctrl+Alt+V.";
            return HotkeyGesture.DefaultQuick;
        }
    }

    internal void Save(HotkeyGesture gesture)
    {
        if (!gesture.IsValid) throw new ArgumentException("Недопустимая быстрая клавиша.", nameof(gesture));
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Не задан каталог настроек.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".quick-hotkey-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new SettingsFile(1, gesture.Modifiers, gesture.VirtualKey));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record SettingsFile(int Schema, uint Modifiers, uint VirtualKey);
}
