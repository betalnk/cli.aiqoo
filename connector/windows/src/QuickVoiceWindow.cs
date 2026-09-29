using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexVoice;

/// <summary>Push-to-talk draft. The source HWND is captured before this window is shown.</summary>
internal sealed class QuickVoiceWindow : Window
{
    private enum Phase { Loading, Listening, Finalizing, Ready, Sending, Closing }

    private readonly QuickTarget? _target;
    private readonly string _targetError;
    private readonly IntPtr _sourceWindow;
    private readonly SpeechCapture _speech = new();
    private readonly MicReadyCue _readyCue = new();
    private readonly TextBlock _state;
    private readonly TextBlock _connection;
    private readonly TextBox _editor;
    private readonly Button _finish;
    private readonly Button _send;
    private readonly Button _cancel;
    private Phase _phase = Phase.Loading;
    private bool _released;
    private bool _closing;
    private bool _captureCleanupStarted;
    private bool _microphoneFailed;
    private string _microphoneError = "";

    internal Task CleanupTask { get; private set; } = Task.CompletedTask;

    internal QuickVoiceWindow(QuickTarget? target, string targetError, IntPtr sourceWindow)
    {
        _target = target;
        _targetError = targetError;
        _sourceWindow = sourceWindow;
        Title = "Codex Voice · быстрый ввод";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Width = 432;
        Height = 320;
        Background = Brush("#F21E1A18");
        TransparencyBackgroundFallback = Brush("#FF1E1A18");
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;

        var heading = new TextBlock
        {
            Text = "БЫСТРЫЙ ГОЛОСОВОЙ ВВОД",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("#FFB86D4F")
        };
        var recipient = new TextBlock
        {
            Text = target is null
                ? "Получатель не подтверждён"
                : $"Получатель: {target.Session.Project} / {target.Session.Name} · {target.Session.ThreadId[..8]}",
            FontSize = 12,
            Foreground = target is null ? Brush("#FFFFA38A") : Brush("#FFF4EFE8"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 5, 0, 0)
        };
        _state = new TextBlock
        {
            Text = target is null ? targetError : "Готовлю локальное распознавание…",
            FontSize = 12,
            Foreground = target is null ? Brush("#FFFFA38A") : Brush("#DAC2B5"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 44,
            Margin = new Thickness(0, 7, 0, 8)
        };
        _editor = new TextBox
        {
            Text = "",
            Watermark = "Распознанный текст появится здесь",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 104,
            Background = Brush("#55322824"),
            BorderBrush = Brush("#97766055"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 8),
            Foreground = Brush("#FFF4EFE8"),
            FontSize = 13
        };
        var editorBackground = Brush("#55322824");
        _editor.Resources["TextControlBackgroundFocused"] = editorBackground;
        _editor.Resources["TextControlBackgroundPointerOver"] = editorBackground;
        _finish = Button("Готово", true);
        _send = Button("Отправить", true);
        _cancel = Button("Отмена", false);
        _finish.Click += (_, _) => _ = FinishAsync(autoSend: false);
        _send.Click += (_, _) => _ = SendAsync(auto: false);
        _cancel.Click += (_, _) => Cancel();
        _connection = new TextBlock
        {
            Text = "Отправка в выбранный терминал; результат проверяется в Codex.",
            FontSize = 10,
            Foreground = Brush("#BDAA9E"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Content = new Border
        {
            Background = Brush("#F21E1A18"),
            BorderBrush = Brush("#8B776A5C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(16, 14),
            Child = new StackPanel
            {
                Children =
                {
                    heading, recipient, _state, _editor,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 7,
                        Margin = new Thickness(0, 11, 0, 0),
                        Children = { _finish, _send, _cancel }
                    },
                    _connection
                }
            }
        };
        UpdateControls();

        _speech.TextChanged += value => Dispatcher.UIThread.Post(() =>
        {
            if (_closing || _phase != Phase.Listening) return;
            _editor.Text = value;
            _editor.CaretIndex = value.Length;
            _state.Text = string.IsNullOrWhiteSpace(value) ? "Слушаю · отпустите клавиши"
                : "Слушаю · черновик · отпустите клавиши";
        });
        _speech.StatusChanged += value => Dispatcher.UIThread.Post(() => OnSpeechStatus(value));
        _speech.FinalizationProgress += (completed, total) => Dispatcher.UIThread.Post(() =>
        {
            if (!_closing && _phase == Phase.Finalizing)
                _state.Text = total > 0
                    ? $"Уточняю запись · {completed} из {total}"
                    : "Уточняю запись…";
        });

        Opened += (_, _) =>
        {
            PlaceAtRightCorner();
            _ = StartSpeechAsync();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Cancel();
        };
        Closed += (_, _) =>
        {
            _closing = true;
            _readyCue.Dispose();
            if (_phase is Phase.Loading or Phase.Listening or Phase.Finalizing)
                BeginCaptureCleanup();
        };
    }

    /// <summary>Called once when any part of the configured key combination is released.</summary>
    internal void OnHotkeyReleased()
    {
        if (_closing || _released) return;
        _released = true;
        if (_phase == Phase.Listening) _ = FinishAsync(autoSend: true);
        else if (_phase == Phase.Loading) _state.Text = "Клавиша отпущена · завершаю запуск микрофона…";
    }

    private async Task StartSpeechAsync()
    {
        try
        {
            await _speech.StartAsync();
            if (_closing) return;
            if (_microphoneFailed) throw new InvalidOperationException(_microphoneError);
            _phase = Phase.Listening;
            UpdateControls();
            if (_released)
            {
                await FinishAsync(autoSend: true);
                return;
            }
            _state.Text = "Слушаю · отпустите клавиши";
            _readyCue.Play();
        }
        catch (Exception exception)
        {
            if (_closing) return;
            _microphoneFailed = true;
            _microphoneError = exception.Message;
            BeginCaptureCleanup();
            await CleanupTask;
            if (_closing) return;
            EnterReady($"Микрофон не запущен: {exception.Message}");
        }
    }

    private void OnSpeechStatus(string value)
    {
        if (_closing) return;
        if (value.StartsWith("Ошибка:", StringComparison.OrdinalIgnoreCase))
        {
            _microphoneFailed = true;
            _microphoneError = value["Ошибка:".Length..].Trim();
            if (_phase == Phase.Listening) _ = FinishAsync(autoSend: false);
            return;
        }
        if (_phase != Phase.Loading || _released) return;
        if (value.StartsWith("Загрузка Whisper", StringComparison.OrdinalIgnoreCase))
            _state.Text = "Загружаю точную локальную модель…";
        else if (value.StartsWith("Слушаю микрофон", StringComparison.OrdinalIgnoreCase))
            _state.Text = "Проверяю готовность микрофона…";
    }

    private async Task FinishAsync(bool autoSend)
    {
        if (_closing || _phase != Phase.Listening) return;
        _phase = Phase.Finalizing;
        _state.Text = "Уточняю запись локально…";
        UpdateControls();
        try
        {
            var final = await _speech.FinishAsync();
            if (_closing) return;
            if (!string.IsNullOrWhiteSpace(final)) _editor.Text = final;
            if (_microphoneFailed)
            {
                EnterReady($"Запись прервана: {_microphoneError}. Проверьте черновик.");
                return;
            }
            if (string.IsNullOrWhiteSpace(_editor.Text))
            {
                EnterReady("Речь не распознана. Можно ввести текст вручную.");
                return;
            }
            _phase = Phase.Ready;
            UpdateControls();
            if (autoSend && _target is not null)
            {
                // This is the only automatic action: direct transport, exact source tab still foreground.
                await SendAsync(auto: true);
            }
            else EnterReady(_target is null ? _targetError : "Текст готов. Проверьте и выберите действие.",
                isError: _target is null);
        }
        catch (Exception exception)
        {
            if (_closing) return;
            BeginCaptureCleanup();
            await CleanupTask;
            if (_closing) return;
            EnterReady($"Не удалось завершить распознавание: {exception.Message}");
        }
    }

    private async Task SendAsync(bool auto)
    {
        if (_closing || _phase != Phase.Ready) return;
        var text = _editor.Text ?? "";
        if (!QuickSendGate.TryAuthorize(_target, text, auto, out var error))
        {
            EnterReady(_target is null ? _targetError : error);
            return;
        }

        // Establish the exact rollout position before any keyboard event is inserted.
        CodexReceiptBaseline? baseline;
        try
        {
            if (!CodexRolloutReceiptVerifier.TryCapture(_target!.Session.ThreadId,
                    out baseline, out error) || baseline is null)
            {
                EnterReady(error);
                return;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EnterReady("Не удалось проверить журнал Codex. Текст не отправлен.");
            return;
        }

        _phase = Phase.Sending;
        _state.Text = "Отправляю текст в Codex…";
        _state.Foreground = Brush("#DAC2B5");
        UpdateControls();
        try
        {
            var result = await ForegroundTerminalSender.SendAsync(_target.Session, text);
            if (_closing) return;
            if (!result.Entered)
            {
                // Even a failed input call may have inserted part of the draft.
                EnterReady(result.Message);
                return;
            }

            Close();
            CodexDeliveryObserver.Observe(baseline, result.SubmittedText, _target.Session);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_closing)
                EnterReady("Не удалось подтвердить получение текста. Проверьте терминал перед повторной отправкой.");
        }
    }

    private void EnterReady(string message, bool isError = true)
    {
        if (_closing) return;
        _phase = Phase.Ready;
        _state.Text = message;
        _state.Foreground = Brush(isError ? "#FFFFA38A" : "#DAC2B5");
        _editor.IsReadOnly = false;
        UpdateControls();
        Activate();
        _editor.Focus();
        _editor.CaretIndex = _editor.Text?.Length ?? 0;
    }

    private void Cancel()
    {
        if (_closing || _phase == Phase.Sending) return;
        _closing = true;
        BeginCaptureCleanup();
        _phase = Phase.Closing;
        Close();
    }

    private void BeginCaptureCleanup()
    {
        if (_captureCleanupStarted) return;
        _captureCleanupStarted = true;
        try { _speech.RequestCancel(); }
        catch { }
        CleanupTask = Task.Run(async () =>
        {
            try { await _speech.CancelAsync(); }
            catch { /* A dismissed toast must not reopen because microphone cleanup failed. */ }
        });
    }

    private void UpdateControls()
    {
        _finish.IsVisible = _phase is Phase.Loading or Phase.Listening or Phase.Finalizing;
        _finish.IsEnabled = _phase == Phase.Listening;
        _send.IsVisible = _phase is Phase.Ready or Phase.Sending;
        _send.IsEnabled = _phase == Phase.Ready && _target is not null;
        _cancel.IsEnabled = _phase != Phase.Sending;
        _editor.IsReadOnly = _phase != Phase.Ready;
    }

    private void PlaceAtRightCorner()
    {
        var screen = Screens.Primary;
        if (_sourceWindow != IntPtr.Zero && GetWindowRect(_sourceWindow, out var rect))
            screen = Screens.ScreenFromPoint(
                new PixelPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)) ?? screen;
        if (screen is null) return;
        var scale = screen.Scaling;
        Position = new PixelPoint(
            screen.WorkingArea.Right - (int)Math.Ceiling(Width * scale) - 18,
            screen.WorkingArea.Bottom - (int)Math.Ceiling(Height * scale) - 18);
    }

    private static Button Button(string label, bool primary) => new()
    {
        Content = label,
        FontSize = 11,
        FontWeight = FontWeight.Medium,
        Padding = new Thickness(10, 6),
        MinWidth = 70,
        Height = 32,
        Background = Brush(primary ? "#FFB86D4F" : "#44352B26"),
        Foreground = Brush(primary ? "#FF1D1512" : "#FFF4EFE8"),
        BorderBrush = Brush(primary ? "#FFC98E70" : "#8B776A5C"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9)
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
