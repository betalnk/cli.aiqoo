using System.Globalization;
using System.Runtime.InteropServices;

namespace CodexVoice;

internal sealed record ForegroundSendResult(bool Entered, string Message, string SubmittedText);

/// <summary>
/// Sends one safe line to the captured, visible terminal. Keyboard insertion is not a Codex receipt.
/// </summary>
internal static class ForegroundTerminalSender
{
    private const string Prefix = "[via Voice Connector] ";
    // Match VoiceDraftStore's recoverable text limit. SendInput is fed in bounded
    // pieces so a long dictation does not become one huge keyboard event batch.
    private const int MaxTextLength = 100_000;
    private const int InputChunkChars = 128;
    // Pace the Windows event queue. SendInput reports insertion into that queue,
    // not consumption by Codex; a large unpaced burst can leave Enter behind text.
    private static readonly TimeSpan InputChunkPause = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan MinPasteSettle = TimeSpan.FromMilliseconds(250);
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyF20 = 0x83;
    private static readonly ushort[] ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    private static readonly SemaphoreSlim SendGate = new(1, 1);

    // A misplaced Enter must not be able to turn shell operators in dictated text into a command.
    private const string ShellMetacharacters = "&|<>^%!$`\"';(){}[]#@*\\";

    internal static async Task<ForegroundSendResult> SendAsync(
        CapturedSession selected, string text, CancellationToken token = default,
        Func<bool>? remoteInputGuard = null)
    {
        var submittedText = PrepareText(text);
        if (submittedText is null)
            return new(false, "Текст содержит перенос строки, управляющий или командный символ либо слишком длинный. Проверьте черновик и отправьте его вручную.", text ?? "");
        if (selected is null || selected.Window == IntPtr.Zero)
            return new(false, "Окно Codex не определено. Текст не введён.", submittedText);

        var enteredText = false;
        var gateHeld = false;
        try
        {
            await SendGate.WaitAsync(token);
            gateHeld = true;
            token.ThrowIfCancellationRequested();

            if (!InputAllowed(remoteInputGuard) || !SessionResolver.TryValidate(selected, out _))
                return new(false, "Выбранная сессия Codex изменилась. Текст не введён.", submittedText);
            if (ModifiersHeld())
                return new(false, "Отпустите клавиши-модификаторы и повторите отправку.", submittedText);

            if (!SetForegroundWindow(selected.Window))
                return new(false, "Не удалось активировать окно Codex. Текст не введён.", submittedText);

            // Let the terminal finish the focus transition, then resolve the visible session again.
            await Task.Delay(60, token);
            if (!IsCurrentTarget(selected, remoteInputGuard) || ModifiersHeld())
                return new(false, "Активное окно или вкладка Codex изменились. Текст не введён.", submittedText);

            token.ThrowIfCancellationRequested();
            // Codex treats rapid key events as a paste. Keep batches small enough for
            // the terminal to consume while checking the exact target between batches.
            // A partial batch is never retried: Codex may already contain part of it.
            for (var offset = 0; offset < submittedText.Length; offset += InputChunkChars)
            {
                if (!IsCurrentTarget(selected, remoteInputGuard) || ModifiersHeld())
                    return new(false, enteredText
                        ? "Текст введён частично, но вкладка Codex изменилась. Enter не отправлен; проверьте терминал."
                        : "Активное окно или вкладка Codex изменились. Текст не введён.", submittedText);
                token.ThrowIfCancellationRequested();
                var length = Math.Min(InputChunkChars, submittedText.Length - offset);
                var unicodeEvents = BuildTextEvents(submittedText.AsSpan(offset, length));
                var inserted = SendInput((uint)unicodeEvents.Length, unicodeEvents,
                    Marshal.SizeOf<KeyboardInputEvent>());
                if (inserted != unicodeEvents.Length)
                    return new(false, "Ввод мог выполниться частично. Проверьте терминал перед повтором; Enter не отправлен.", submittedText);
                enteredText = true;
                if (offset + length < submittedText.Length)
                    await Task.Delay(InputChunkPause, token);
            }

            // F20 has no default Codex composer action. It flushes pending text without
            // triggering this PC's Lightshot shortcut (the former End key did).
            if (!IsCurrentTarget(selected, remoteInputGuard) || ModifiersHeld())
                return new(false, "Текст введён, но активная сессия изменилась. Enter не отправлен.", submittedText);
            var flushEvents = new[]
            {
                Keyboard(virtualKey: VirtualKeyF20),
                Keyboard(virtualKey: VirtualKeyF20, flags: KeyEventKeyUp)
            };
            var flushInserted = SendInput((uint)flushEvents.Length, flushEvents,
                Marshal.SizeOf<KeyboardInputEvent>());
            if (flushInserted != flushEvents.Length)
            {
                if (flushInserted > 0)
                {
                    var release = new[] { Keyboard(virtualKey: VirtualKeyF20, flags: KeyEventKeyUp) };
                    _ = SendInput(1, release, Marshal.SizeOf<KeyboardInputEvent>());
                }
                return new(false, "Текст введён, но завершить вставку не удалось. Enter не отправлен.", submittedText);
            }

            // The Codex paste detector keeps Enter as a newline for 120 ms after a
            // burst. A non-character flush can be ignored by an older terminal, so
            // also wait past that window. Long messages get extra drain time.
            await Task.Delay(PasteSettleDelay(submittedText.Length), token);
            if (!IsCurrentTarget(selected, remoteInputGuard) || ModifiersHeld())
                return new(false, "Текст введён, но активная сессия изменилась. Enter не отправлен.", submittedText);

            token.ThrowIfCancellationRequested();
            var enterEvents = new[]
            {
                Keyboard(virtualKey: VirtualKeyReturn),
                Keyboard(virtualKey: VirtualKeyReturn, flags: KeyEventKeyUp)
            };
            var enterInserted = SendInput((uint)enterEvents.Length, enterEvents,
                Marshal.SizeOf<KeyboardInputEvent>());
            if (enterInserted == 0)
                return new(false, "Текст введён, но Enter не удалось передать. Проверьте терминал перед повтором.", submittedText);
            if (enterInserted < enterEvents.Length)
            {
                // The key-down may already have submitted the line; release it without retrying Enter.
                var release = new[] { Keyboard(virtualKey: VirtualKeyReturn, flags: KeyEventKeyUp) };
                _ = SendInput(1, release, Marshal.SizeOf<KeyboardInputEvent>());
                return new(false, "Enter передан частично. Проверьте Codex перед повтором; приём сообщения не подтверждён.", submittedText);
            }

            return new(true, "Enter передан Windows. Приём сообщения Codex не подтверждён.", submittedText);
        }
        catch (OperationCanceledException)
        {
            return new(false, enteredText
                ? "Текст введён, но отправка отменена до Enter. Проверьте терминал."
                : "Отправка отменена. Текст не введён.", submittedText);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, enteredText
                ? "Текст мог быть введён, но Enter не подтверждён. Проверьте терминал перед повтором."
                : "Не удалось проверить терминал или передать текст. Отправка остановлена.", submittedText);
        }
        finally
        {
            if (gateHeld) SendGate.Release();
        }
    }

    internal static string? PrepareText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return null;
        foreach (var character in text)
        {
            var category = char.GetUnicodeCategory(character);
            if ((char.IsWhiteSpace(character) && character != ' ') || char.IsControl(character)
                || char.IsSurrogate(character) || category is UnicodeCategory.Format
                    or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                || ShellMetacharacters.Contains(character))
                return null;
        }

        var line = text.Trim(' ');
        return line.Length == 0 ? null : Prefix + line;
    }

    private static bool IsCurrentTarget(CapturedSession selected, Func<bool>? remoteInputGuard) =>
        InputAllowed(remoteInputGuard) && GetForegroundWindow() == selected.Window
        && SessionResolver.TryValidate(selected, out _);

    private static bool InputAllowed(Func<bool>? remoteInputGuard) =>
        remoteInputGuard is null || remoteInputGuard();

    private static bool ModifiersHeld()
    {
        foreach (var key in ModifierKeys)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }

    internal static TimeSpan PasteSettleDelay(int textLength) =>
        MinPasteSettle + TimeSpan.FromMilliseconds(Math.Min(1500, Math.Max(0, textLength) / 16));

    private static KeyboardInputEvent[] BuildTextEvents(ReadOnlySpan<char> text)
    {
        var events = new KeyboardInputEvent[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            events[index * 2] = Keyboard(scanCode: text[index], flags: KeyEventUnicode);
            events[index * 2 + 1] = Keyboard(scanCode: text[index], flags: KeyEventUnicode | KeyEventKeyUp);
        }
        return events;
    }

    private static KeyboardInputEvent Keyboard(ushort virtualKey = 0, ushort scanCode = 0, uint flags = 0) =>
        new()
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardEvent { VirtualKey = virtualKey, ScanCode = scanCode, Flags = flags }
        };

    // INPUT's union starts at offset 8 and occupies 32 bytes on this x64 Windows target.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInputEvent
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardEvent Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] KeyboardInputEvent[] inputs, int inputSize);
}
