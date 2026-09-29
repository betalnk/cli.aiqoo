using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace CodexVoice;

internal sealed record HotkeyChangeResult(bool Success, string Error);

internal sealed class HotkeyListener : IDisposable
{
    private const int MainHotkeyId = 0x434f;
    private const int QuickHotkeyId = 0x4351;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint WmChangeQuick = 0x0400 + 17;
    private const uint ModNoRepeat = 0x4000;
    private readonly Thread _thread;
    private readonly Action<CapturedSession> _onCapture;
    private readonly Action<IntPtr> _onQuickPress;
    private readonly Action _onQuickRelease;
    private readonly Action<string> _onError;
    private readonly ManualResetEventSlim _ready = new();
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly object _changeLock = new();
    private HotkeyGesture _quickGesture;
    private HotkeyGesture _requestedGesture;
    private TaskCompletionSource<HotkeyChangeResult>? _changeCompletion;
    private uint _threadId;
    private int _quickHeld;
    private bool _mainRegistered;
    private bool _quickRegistered;
    private volatile bool _disposed;

    public HotkeyListener(Action<CapturedSession> onCapture, Action<IntPtr> onQuickPress,
        Action onQuickRelease, Action<string> onError, HotkeyGesture quickGesture)
    {
        if (!quickGesture.IsValid) throw new ArgumentException("Недопустимая быстрая клавиша.", nameof(quickGesture));
        _onCapture = onCapture;
        _onQuickPress = onQuickPress;
        _onQuickRelease = onQuickRelease;
        _onError = onError;
        _quickGesture = quickGesture;
        _thread = new Thread(Run) { Name = "CodexVoice hotkeys", IsBackground = true };
        _thread.Start();
    }

    internal async Task<HotkeyChangeResult> ChangeQuickHotkeyAsync(HotkeyGesture gesture)
    {
        if (!gesture.IsValid) return new(false, "Для быстрой записи выберите две клавиши-модификатора и букву, цифру, F1–F12 или пробел.");
        await _updateGate.WaitAsync();
        try
        {
            if (!_ready.Wait(TimeSpan.FromSeconds(3)) || _disposed)
                return new(false, "Обработчик горячих клавиш недоступен.");
            var completion = new TaskCompletionSource<HotkeyChangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_changeLock)
            {
                _requestedGesture = gesture;
                _changeCompletion = completion;
            }
            if (!PostThreadMessage(_threadId, WmChangeQuick, IntPtr.Zero, IntPtr.Zero))
            {
                lock (_changeLock) _changeCompletion = null;
                return new(false, "Не удалось изменить горячую клавишу.");
            }
            return await completion.Task;
        }
        finally { _updateGate.Release(); }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _mainRegistered = RegisterHotKey(IntPtr.Zero, MainHotkeyId,
            HotkeyGesture.Main.Modifiers | ModNoRepeat, HotkeyGesture.Main.VirtualKey);
        _quickRegistered = RegisterHotKey(IntPtr.Zero, QuickHotkeyId,
            _quickGesture.Modifiers | ModNoRepeat, _quickGesture.VirtualKey);
        _ready.Set();
        if (!_mainRegistered)
            ReportError("Ctrl+Alt+Пробел уже занят другой программой.");
        if (!_quickRegistered)
            ReportError($"{_quickGesture.Display} уже занята другой программой. Измените быструю клавишу в трее.");

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == WmChangeQuick)
                {
                    ChangeQuickHotkeyOnThread();
                    continue;
                }
                if (message.message != WmHotkey) continue;
                if (message.wParam == (IntPtr)MainHotkeyId)
                {
                    if (SessionResolver.TryCapture(out var session, out var error) && session is not null)
                        Dispatcher.UIThread.Post(() => _onCapture(session));
                    else
                        ReportError(error);
                }
                else if (message.wParam == (IntPtr)QuickHotkeyId
                         && Interlocked.CompareExchange(ref _quickHeld, 1, 0) == 0)
                {
                    var foreground = GetForegroundWindow();
                    var gesture = _quickGesture;
                    Dispatcher.UIThread.Post(() => _onQuickPress(foreground));
                    _ = WatchReleaseAsync(gesture);
                }
            }
        }
        finally
        {
            if (_mainRegistered) UnregisterHotKey(IntPtr.Zero, MainHotkeyId);
            if (_quickRegistered) UnregisterHotKey(IntPtr.Zero, QuickHotkeyId);
            lock (_changeLock)
            {
                _changeCompletion?.TrySetResult(new(false, "Обработчик горячих клавиш остановлен."));
                _changeCompletion = null;
            }
        }
    }

    private void ChangeQuickHotkeyOnThread()
    {
        HotkeyGesture next;
        TaskCompletionSource<HotkeyChangeResult>? completion;
        lock (_changeLock)
        {
            next = _requestedGesture;
            completion = _changeCompletion;
            _changeCompletion = null;
        }
        if (completion is null) return;
        if (_quickHeld != 0)
        {
            completion.TrySetResult(new(false, "Отпустите клавиши и завершите текущую запись."));
            return;
        }
        if (next == _quickGesture && _quickRegistered)
        {
            completion.TrySetResult(new(true, ""));
            return;
        }
        if (_quickRegistered) UnregisterHotKey(IntPtr.Zero, QuickHotkeyId);
        var previous = _quickGesture;
        var registered = RegisterHotKey(IntPtr.Zero, QuickHotkeyId,
            next.Modifiers | ModNoRepeat, next.VirtualKey);
        if (registered)
        {
            _quickGesture = next;
            _quickRegistered = true;
            completion.TrySetResult(new(true, ""));
            return;
        }
        _quickRegistered = RegisterHotKey(IntPtr.Zero, QuickHotkeyId,
            previous.Modifiers | ModNoRepeat, previous.VirtualKey);
        completion.TrySetResult(new(false,
            _quickRegistered
                ? $"{next.Display} уже занята другой программой."
                : $"{next.Display} занята, прежнюю клавишу тоже не удалось восстановить."));
    }

    private async Task WatchReleaseAsync(HotkeyGesture gesture)
    {
        try
        {
            while (!_disposed && IsHeld(gesture))
                await Task.Delay(20);
            if (!_disposed) Dispatcher.UIThread.Post(_onQuickRelease);
        }
        finally { Interlocked.Exchange(ref _quickHeld, 0); }
    }

    private static bool IsHeld(HotkeyGesture gesture) =>
        Down((int)gesture.VirtualKey)
        && ((gesture.Modifiers & HotkeyGesture.Control) == 0 || Down(0x11))
        && ((gesture.Modifiers & HotkeyGesture.Alt) == 0 || Down(0x12))
        && ((gesture.Modifiers & HotkeyGesture.Shift) == 0 || Down(0x10));

    private static bool Down(int virtualKey) => GetAsyncKeyState(virtualKey) < 0;

    private void ReportError(string error) => Dispatcher.UIThread.Post(() => _onError(error));

    public void Dispose()
    {
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(1));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
