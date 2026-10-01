using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CodexVoice;

internal sealed class VoiceOverlay : Window
{
    private enum Phase { Loading, Listening, Finalizing, Editing, PreparingSend, Sending, StartFailed, Closing }
    private enum MicrophoneState { Inactive, Loading, Ready, Error }

    private readonly CapturedSession _captured;
    private readonly string _draftOriginThreadId;
    private readonly SessionPlaybackController? _playback;
    private readonly VoiceDraftStore? _draftStore;
    private readonly VoiceDraft? _restoredDraft;
    private readonly Func<BackgroundSendRequest, Task>? _backgroundSend;
    private readonly List<CapturedSession> _sessions;
    private readonly bool _preview;
    private readonly SpeechCapture _speech = new();
    private readonly MicReadyCue _micReadyCue = new();
    private readonly VoiceOrb _orb = new();
    private readonly StackPanel _recipientArea;
    private readonly StackPanel _recipientSelector;
    private readonly TextBlock _recipientContext;
    private readonly TextBlock _recipientName;
    private readonly TextBlock _recipientIndex;
    private readonly Button _confirmRecipientButton;
    private readonly TextBlock _codexStatus;
    private readonly TextBox _editor;
    private readonly TextBlock _status;
    private readonly TextBlock _microphoneStatus;
    private readonly TextBlock _hints;
    private readonly Button _sendButton;
    private readonly Button _closeButton;
    private readonly Button _previousAnswerButton;
    private readonly Button _playAnswerButton;
    private readonly Button _nextAnswerButton;
    private readonly CheckBox _autoReadCheckBox;
    private readonly TextBlock _answerStatus;
    private DispatcherTimer? _previewTimer;
    private Phase _phase = Phase.Loading;
    private MicrophoneState _microphoneState = MicrophoneState.Inactive;
    private string _microphoneError = "";
    private bool _closing;
    private bool _cleanupStarted;
    private bool _sendAfterFinalization;
    private bool _closeAfterFinalization;
    private Task<string>? _finalizationTask;
    private bool _backgroundSpeechFailure;
    private string _latestTranscriptPreview = "";
    private int _selectedIndex;
    private Point? _swipeStart;
    private Point _swipePosition;
    private Task _playbackBindTask = Task.CompletedTask;
    private string? _playbackBindingThreadId;
    private bool _updatingPlaybackUi;
    private bool _autoReadRequestPending;
    private int _autoReadRequestVersion;
    private bool _recipientConfirmed;
    private string? _confirmedThreadId;
    private CodexConnectionStatus? _codexConnection;

    internal Task CleanupTask { get; private set; } = Task.CompletedTask;
    internal bool WantsAutoRead => _autoReadCheckBox.IsChecked == true;

    public VoiceOverlay(CapturedSession target, SessionPlaybackController? playback = null,
        bool preview = false, Action? openHotkeySettings = null,
        Action? openCliAccount = null,
        VoiceDraftStore? draftStore = null, VoiceDraft? restoredDraft = null,
        Func<BackgroundSendRequest, Task>? backgroundSend = null)
    {
        _captured = target;
        _draftOriginThreadId = restoredDraft?.OriginalThreadId ?? target.ThreadId;
        _playback = playback;
        _preview = preview;
        _draftStore = draftStore;
        _restoredDraft = restoredDraft;
        _backgroundSend = backgroundSend;
        _recipientConfirmed = preview;
        if (preview)
            _sessions = [target, target with { Name = "Другая вкладка", Project = "Предпросмотр" }];
        else
        {
            try
            {
                _sessions = SessionResolver.ListOpenSessions(target).ToList();
            }
            catch { _sessions = [target]; }
            if (SessionResolver.TryValidate(target, out _))
            {
                _recipientConfirmed = true;
                _confirmedThreadId = target.ThreadId;
            }
            if (restoredDraft is not null)
            {
                // Keep the original recipient snapshot. A new tab with the same title
                // must never become the recipient of a recovered message implicitly.
                var saved = restoredDraft.Recipient.ToCapturedSession();
                var index = _sessions.FindIndex(item => item.ThreadId == saved.ThreadId
                    && item.Window == saved.Window
                    && item.WindowProcessId == saved.WindowProcessId
                    && item.WindowProcessStartTicks == saved.WindowProcessStartTicks);
                if (index < 0)
                {
                    _sessions.Insert(0, saved);
                    _selectedIndex = 0;
                }
                else
                {
                    _sessions[index] = saved;
                    _selectedIndex = index;
                }
                _recipientConfirmed = false;
                _confirmedThreadId = null;
            }
        }
        Title = "Codex Voice";
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brush("#E3161211");
        TransparencyBackgroundFallback = Brush("#FF161211");
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;

        _editor = new TextBox
        {
            Width = 560,
            Height = 96,
            Text = "",
            Watermark = "Нажмите здесь, чтобы править",
            IsReadOnly = true,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            Background = Brush("#55322824"),
            BorderBrush = Brush("#97766055"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(15, 12),
            Foreground = Brush("#FFF4EFE8"),
            FontSize = 15,
            FontWeight = FontWeight.Medium,
            Margin = new Thickness(0, 0, 0, 12)
        };
        if (restoredDraft is not null)
        {
            _editor.Text = restoredDraft.Text;
            _editor.Watermark = "Введите сообщение…";
        }
        // Fluent uses separate template brushes while the field is focused or hovered.
        // Keep those states on the same surface as the idle editor during finalization and send.
        var editorBackground = Brush("#55322824");
        _editor.Resources["TextControlBackgroundFocused"] = editorBackground;
        _editor.Resources["TextControlBackgroundPointerOver"] = editorBackground;
        _editor.AddHandler(InputElement.PointerPressedEvent, OnEditorPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        _status = new TextBlock
        {
            Text = "Готовлю распознавание…",
            Foreground = Brush("#DAC2B5"),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };
        _microphoneStatus = new TextBlock
        {
            Foreground = Brush("#DAC2B5"),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false
        };
        _hints = new TextBlock
        {
            Foreground = Brush("#BDAA9E"),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var stage = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _recipientContext = new TextBlock
        {
            Foreground = Brush("#FFB86D4F"),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };
        _recipientName = new TextBlock
        {
            Foreground = Brush("#FFF4EFE8"),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 464,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _recipientIndex = new TextBlock
        {
            Foreground = Brush("#BDAA9E"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
            IsVisible = _sessions.Count > 1
        };
        _recipientArea = new StackPanel
        {
            Width = 464,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _recipientContext, _recipientName, _recipientIndex }
        };
        _confirmRecipientButton = ActionButton("Подтвердить сессию", primary: false);
        _confirmRecipientButton.Width = 166;
        _confirmRecipientButton.Height = 30;
        _confirmRecipientButton.FontSize = 11;
        _confirmRecipientButton.Margin = new Thickness(0, 4, 0, 4);
        _confirmRecipientButton.HorizontalAlignment = HorizontalAlignment.Center;
        _confirmRecipientButton.IsVisible = !_preview && restoredDraft is not null;
        AutomationProperties.SetName(_confirmRecipientButton, "Подтвердить получателя по точному идентификатору Codex");
        _confirmRecipientButton.Click += (_, _) => ConfirmRecipient();
        _codexStatus = new TextBlock
        {
            Text = "Codex: проверяю соединение…",
            Foreground = Brush("#DAC2B5"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            IsVisible = !_preview
        };
        _recipientArea.PointerPressed += OnRecipientPointerPressed;
        _recipientArea.PointerMoved += OnRecipientPointerMoved;
        _recipientArea.PointerReleased += OnRecipientPointerReleased;
        _recipientArea.PointerCaptureLost += (_, _) => _swipeStart = null;

        var previous = ArrowButton(right: false);
        previous.IsVisible = _sessions.Count > 1;
        previous.Click += (_, _) => SelectOffset(-1);
        var next = ArrowButton(right: true);
        next.IsVisible = _sessions.Count > 1;
        next.Click += (_, _) => SelectOffset(1);
        _recipientSelector = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { previous, _recipientArea, next }
        };
        UpdateRecipient();
        stage.Children.Add(_recipientSelector);
        stage.Children.Add(_confirmRecipientButton);
        stage.Children.Add(_codexStatus);

        _orb.HorizontalAlignment = HorizontalAlignment.Center;
        _orb.Margin = new Thickness(0, 8, 0, 12);
        _orb.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(_orb).Properties.IsLeftButtonPressed) return;
            args.Handled = true;
            if (_preview) Close();
            else if (_phase == Phase.Editing && string.IsNullOrWhiteSpace(_editor.Text))
                _ = StartSpeechAsync();
            else _ = SubmitAsync(closeAfterSend: true);
        };
        stage.Children.Add(_orb);
        stage.Children.Add(_editor);
        stage.Children.Add(_status);
        stage.Children.Add(_microphoneStatus);
        _sendButton = ActionButton("Отправить · остаться", primary: true);
        _sendButton.Width = 188;
        _closeButton = ActionButton("Закрыть", primary: false);
        AutomationProperties.SetName(_sendButton, "Отправить сообщение и оставить экран открытым");
        AutomationProperties.SetName(_closeButton, "Закрыть без отправки");
        ToolTip.SetTip(_sendButton, "Отправить и оставить экран открытым");
        ToolTip.SetTip(_closeButton, "Закрыть без отправки · Esc");
        _sendButton.Click += (_, _) =>
        {
            if (_preview) Close();
            else _ = SubmitAsync(closeAfterSend: false);
        };
        _closeButton.Click += (_, _) =>
        {
            if (_preview) Close();
            else _ = CancelAsync();
        };
        stage.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { _sendButton, _closeButton }
        });
        _previousAnswerButton = ActionButton("‹", primary: false);
        _previousAnswerButton.Width = 38;
        _previousAnswerButton.Height = 36;
        _previousAnswerButton.FontSize = 20;
        _playAnswerButton = ActionButton("▶ Ответ ИИ", primary: false);
        _playAnswerButton.Width = 126;
        _playAnswerButton.Height = 36;
        _nextAnswerButton = ActionButton("›", primary: false);
        _nextAnswerButton.Width = 38;
        _nextAnswerButton.Height = 36;
        _nextAnswerButton.FontSize = 20;
        _autoReadCheckBox = new CheckBox
        {
            Content = "Автопрослушка",
            Foreground = Brush("#E2CEC1"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        AutomationProperties.SetName(_previousAnswerButton, "Предыдущий ответ ИИ");
        AutomationProperties.SetName(_playAnswerButton, "Прослушать или остановить ответ ИИ");
        AutomationProperties.SetName(_nextAnswerButton, "Следующий ответ ИИ");
        AutomationProperties.SetName(_autoReadCheckBox, "Автоматически озвучивать новые ответы выбранной сессии");
        ToolTip.SetTip(_autoReadCheckBox, "Озвучивать новые ответы выбранной сессии даже после закрытия экрана");
        _previousAnswerButton.Click += async (_, _) => await PlayAnswerAsync(-1);
        _playAnswerButton.Click += async (_, _) => await PlayAnswerAsync(0);
        _nextAnswerButton.Click += async (_, _) => await PlayAnswerAsync(1);
        _autoReadCheckBox.IsCheckedChanged += (_, _) =>
        {
            if (_updatingPlaybackUi || _playback is null) return;
            if (_autoReadCheckBox.IsChecked == true) _ = EnableAutoReadAsync();
            else
            {
                _autoReadRequestVersion++;
                _autoReadRequestPending = false;
                _playback.AutoRead = false;
            }
        };
        stage.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 7,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { _previousAnswerButton, _playAnswerButton, _nextAnswerButton, _autoReadCheckBox }
        });
        _answerStatus = new TextBlock
        {
            Foreground = Brush("#BDAA9E"),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 550,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stage.Children.Add(_answerStatus);
        stage.Children.Add(_hints);
        if (_playback is not null)
        {
            _playback.StateChanged += UpdatePlaybackUi;
            _playback.Error += ShowPlaybackError;
        }
        UpdatePlaybackUi();
        ChangePhase(Phase.Loading);
        if (restoredDraft is not null)
        {
            ChangePhase(Phase.Editing);
            var source = restoredDraft.OriginalThreadId == target.ThreadId
                ? "этой сессии" : $"сессии {restoredDraft.OriginalThreadId[^8..]}";
            _status.Text = string.IsNullOrWhiteSpace(restoredDraft.Text)
                ? $"Запись {source} прервалась до появления текста"
                : restoredDraft.Status == "Получение Codex неизвестно"
                    ? $"Черновик {source}: Enter уже передан · проверьте Codex перед повтором"
                : restoredDraft.Status is not ("Ожидает отправки" or "Ожидает уточнения записи"
                    or "Распознано; отправка ещё не подтверждена" or "Сохранён для проверки")
                    ? $"{restoredDraft.Status} · черновик {source} сохранён"
                : $"Черновик {source} восстановлен · проверьте Codex и подтвердите получателя";
        }
        var accountButton = ActionButton("Аккаунт CLI ↗", primary: false);
        accountButton.FontSize = 10;
        accountButton.Height = 28;
        accountButton.Padding = new Thickness(9, 3);
        accountButton.IsVisible = !_preview && openCliAccount is not null;
        AutomationProperties.SetName(accountButton, "Открыть отдельный аккаунт CLI в браузере");
        accountButton.Click += (_, _) => LeaveForAccount(openCliAccount);
        var hotkeySettings = ActionButton("Клавиши", primary: false);
        hotkeySettings.FontSize = 10;
        hotkeySettings.Height = 28;
        hotkeySettings.Padding = new Thickness(9, 3);
        hotkeySettings.IsVisible = !_preview && openHotkeySettings is not null;
        AutomationProperties.SetName(hotkeySettings, "Настроить клавиши быстрого голосового ввода");
        hotkeySettings.Click += (_, _) => openHotkeySettings?.Invoke();
        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 20, 0),
            Children = { accountButton, hotkeySettings }
        };
        corner.IsVisible = !_preview;
        Content = new Grid { Children = { stage, corner } };

        _speech.LevelChanged += level => Dispatcher.UIThread.Post(() =>
        {
            if (!_closing && _phase == Phase.Listening) _orb.SetLevel(level);
        });
        _speech.TextChanged += value =>
        {
            Volatile.Write(ref _latestTranscriptPreview, value);
            Dispatcher.UIThread.Post(() =>
            {
                if (_closing || _phase != Phase.Listening) return;
                _editor.Text = value;
                _editor.CaretIndex = value.Length;
                _status.Text = string.IsNullOrWhiteSpace(value) ? "Слушаю" : "Слушаю · черновик";
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_closing && _phase == Phase.Listening && _editor.Text == value)
                        _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
                }, DispatcherPriority.Loaded);
            });
        };
        _speech.StatusChanged += value => Dispatcher.UIThread.Post(() => ShowSpeechStatus(value));
        _speech.FinalizationProgress += (completed, total) => Dispatcher.UIThread.Post(() =>
        {
            if (_closing || _phase != Phase.Finalizing) return;
            _status.Text = total > 0
                ? $"Уточняю запись · {completed} из {total}"
                : "Уточняю запись…";
        });

        Opened += async (_, _) =>
        {
            PlaceOverTargetScreen();
            Focus();
            if (_preview) StartPreview();
            else
            {
                UpdateTargetStatus();
                if (_restoredDraft is not null) FocusEditor();
                else
                {
                    _ = BindPlaybackForUiAsync();
                    await StartSpeechAsync();
                }
            }
        };
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Closed += (_, _) =>
        {
            var shouldCancel = !_closing && !_preview && (_phase is Phase.Loading or Phase.Listening or Phase.Finalizing);
            _closing = true;
            _previewTimer?.Stop();
            _micReadyCue.Dispose();
            if (_playback is not null)
            {
                _playback.StateChanged -= UpdatePlaybackUi;
                _playback.Error -= ShowPlaybackError;
            }
            if (shouldCancel) BeginSpeechCleanup();
        };
    }

    private CapturedSession SelectedSession => _sessions[_selectedIndex];

    private static Button ArrowButton(bool right)
    {
        var button = new Button
        {
            Width = 40,
            Height = 38,
            Padding = new Thickness(0),
            Background = Brush("#00000000"),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new Chevron(right)
        };
        button.PointerEntered += (_, _) => button.Background = Brush("#45352B26");
        button.PointerExited += (_, _) => button.Background = Brush("#00000000");
        ToolTip.SetTip(button, right ? "Следующая сессия" : "Предыдущая сессия");
        return button;
    }

    private static Button ActionButton(string label, bool primary)
    {
        var button = new Button
        {
            Width = 128,
            Height = 44,
            Content = label,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush(primary ? "#FFB86D4F" : "#44352B26"),
            Foreground = Brush(primary ? "#FF1D1512" : "#FFF4EFE8"),
            BorderBrush = Brush(primary ? "#FFC98E70" : "#8B776A5C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.PointerEntered += (_, _) => button.Background = Brush(primary ? "#FFC98E70" : "#6654453C");
        button.PointerExited += (_, _) => button.Background = Brush(primary ? "#FFB86D4F" : "#44352B26");
        return button;
    }

    private sealed class Chevron(bool right) : Control
    {
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var pen = new Pen(Brush("#E2CEC1"), 1.8);
            var x1 = right ? 4.0 : 10.0;
            var x2 = right ? 10.0 : 4.0;
            context.DrawLine(pen, new Point(x1, 2), new Point(x2, 9));
            context.DrawLine(pen, new Point(x2, 9), new Point(x1, 16));
        }

        protected override Size MeasureOverride(Size availableSize) => new(14, 18);
    }

    private void UpdateRecipient()
    {
        var selected = SelectedSession;
        _recipientContext.Text = _recipientConfirmed
            ? "Кому: подтверждённая сессия"
            : "Кому: проверьте выбранную вкладку";
        var suffix = selected.ThreadId.Length >= 8 ? selected.ThreadId[^8..] : selected.ThreadId;
        _recipientName.Text = $"{selected.Project}  /  {selected.Name}  ·  {suffix}";
        ToolTip.SetTip(_recipientName, selected.ThreadId);
        _recipientIndex.Text = $"{_selectedIndex + 1} / {_sessions.Count}";
        _confirmRecipientButton.Content = _recipientConfirmed ? "✓ Подтверждена" : "Подтвердить сессию";
    }

    private void ConfirmRecipient()
    {
        if (_preview || _closing) return;
        var selected = SelectedSession;
        if (selected.Window == IntPtr.Zero)
        {
            _status.Text = "Окно этой сессии не найдено.";
            return;
        }
        if (!SessionResolver.TryValidate(selected, out var error))
        {
            _status.Text = error;
            return;
        }
        _recipientConfirmed = true;
        _confirmedThreadId = selected.ThreadId;
        UpdateRecipient();
        UpdateTargetStatus();
        ChangePhase(_phase);
        _status.Text = "Получатель подтверждён";
        _ = BindPlaybackForUiAsync();
        UpdatePlaybackUi();
    }

    private bool TryValidateSelectedRecipient(out string error)
    {
        var selected = SelectedSession;
        if (!_recipientConfirmed || _confirmedThreadId != selected.ThreadId)
        {
            error = "Получатель не подтверждён. Текст сохранён для правки.";
            return false;
        }
        if (selected.Window == IntPtr.Zero)
        {
            error = "Окно этой сессии не найдено. Текст сохранён для правки.";
            return false;
        }
        if (!SessionResolver.TryValidate(selected, out error))
            return false;
        error = "";
        return true;
    }

    private void UpdateTargetStatus()
    {
        if (_closing || _preview) return;
        var available = SessionResolver.TryValidate(SelectedSession, out var error);
        _codexConnection = available ? CodexConnectionStatus.Connected
            : CodexConnectionStatus.Unavailable;
        _codexStatus.Text = available ? "Codex: выбранная консоль найдена"
            : "Codex: выбранная консоль изменилась";
        _codexStatus.Foreground = Brush(available
            ? "#FFA7D8AB" : "#FFFFB18F");
        ToolTip.SetTip(_codexStatus, available
            ? "Перед отправкой окно и сессия будут проверены снова." : error);
        ChangePhase(_phase);
    }

    private void SelectOffset(int offset)
    {
        if (!_recipientSelector.IsEnabled || _closing) return;
        if (!_preview)
        {
            var previous = SelectedSession;
            var current = SessionResolver.ListOpenSessions(_captured);
            if (current.Count > 0)
            {
                _sessions.Clear();
                _sessions.AddRange(current);
                var same = _sessions.FindIndex(item => item.ThreadId == previous.ThreadId
                    && item.Window == previous.Window);
                _selectedIndex = same >= 0 ? same : 0;
            }
        }
        if (_sessions.Count < 2) return;
        _selectedIndex = (_selectedIndex + offset + _sessions.Count) % _sessions.Count;
        var selected = SelectedSession;
        _recipientConfirmed = _preview || (_restoredDraft is null
            && SessionResolver.TryValidate(selected, out _));
        _confirmedThreadId = _recipientConfirmed ? selected.ThreadId : null;
        UpdateRecipient();
        UpdateTargetStatus();
        ChangePhase(_phase);
        if (_phase == Phase.Editing) _status.Text = "Можно править";
        _autoReadRequestVersion++;
        _autoReadRequestPending = false;
        if (_playback is not null) _playback.AutoRead = false;
        _updatingPlaybackUi = true;
        _autoReadCheckBox.IsChecked = false;
        _updatingPlaybackUi = false;
        UpdatePlaybackUi();
    }

    private Task BindPlaybackAsync(string threadId)
    {
        if (_playback is null) return Task.CompletedTask;
        if (_playbackBindingThreadId == threadId &&
            (_playback.IsReady || !_playbackBindTask.IsCompleted))
            return _playbackBindTask;
        _playbackBindingThreadId = threadId;
        return _playbackBindTask = _playback.SetSelectedSessionAsync(threadId);
    }

    private async Task BindPlaybackForUiAsync()
    {
        if (_playback is null || (!_preview && !_recipientConfirmed)) return;
        try { await BindPlaybackAsync(SelectedSession.ThreadId); }
        catch { /* Controller reports the specific history error through Error. */ }
    }

    private async Task EnableAutoReadAsync()
    {
        if (_playback is null || (!_preview && !_recipientConfirmed)) return;
        var threadId = SelectedSession.ThreadId;
        var requestVersion = ++_autoReadRequestVersion;
        _autoReadRequestPending = true;
        _answerStatus.Text = "Готовлю автопрослушку…";
        try
        {
            await BindPlaybackAsync(threadId);
            if (requestVersion == _autoReadRequestVersion
                && _autoReadCheckBox.IsChecked == true && SelectedSession.ThreadId == threadId
                && _playback.SelectedThreadId == threadId && _playback.IsReady)
                _playback.AutoRead = true;
        }
        catch { /* Controller reports the specific history error through Error. */ }
        finally
        {
            if (requestVersion == _autoReadRequestVersion)
            {
                _autoReadRequestPending = false;
                UpdatePlaybackUi();
                if (_closing && !_playback.AutoRead && _playback.SelectedThreadId == threadId)
                    _playback.StopIdleMonitoring();
            }
        }
    }

    private async Task PlayAnswerAsync(int offset)
    {
        if (_playback is null || _closing || (!_preview && !_recipientConfirmed)) return;
        if (offset == 0 && _playback.IsPlaying)
        {
            _playback.StopPlayback();
            return;
        }
        try
        {
            await BindPlaybackAsync(SelectedSession.ThreadId);
            if (_closing) return;
            var answer = offset == 0
                ? await _playback.PlayCurrentAsync()
                : await _playback.MoveAndPlayAsync(offset);
            if (answer is null) _answerStatus.Text = "Здесь больше нет ответов ИИ";
        }
        catch { /* Controller reports the specific history error through Error. */ }
    }

    private void UpdatePlaybackUi()
    {
        if (_playback is null)
        {
            _previousAnswerButton.IsEnabled = false;
            _playAnswerButton.IsEnabled = false;
            _nextAnswerButton.IsEnabled = false;
            _autoReadCheckBox.IsEnabled = false;
            _answerStatus.Text = "";
            return;
        }
        if (!_preview && !_recipientConfirmed)
        {
            _previousAnswerButton.IsEnabled = false;
            _playAnswerButton.IsEnabled = false;
            _nextAnswerButton.IsEnabled = false;
            _autoReadCheckBox.IsEnabled = false;
            _answerStatus.Text = "Подтвердите сессию для ответов ИИ";
            return;
        }
        var answer = _playback.Current;
        var ready = _playback.IsReady && _playback.SelectedThreadId == SelectedSession.ThreadId;
        _previousAnswerButton.IsEnabled = ready && answer is not null;
        _playAnswerButton.IsEnabled = ready && answer is not null;
        _nextAnswerButton.IsEnabled = ready && answer is not null;
        _playAnswerButton.Content = _playback.IsPlaying ? "■ Остановить" : "▶ Ответ ИИ";
        _autoReadCheckBox.IsEnabled = !_preview;
        _updatingPlaybackUi = true;
        _autoReadCheckBox.IsChecked = _playback.AutoRead || _autoReadRequestPending;
        _updatingPlaybackUi = false;
        _answerStatus.Text = !ready ? "История ответов загружается…"
            : answer is null ? "Ответов ИИ пока нет"
            : _playback.IsPlaying ? "Озвучиваю ответ ИИ"
            : ShortAnswer(answer.Text);
    }

    private static string ShortAnswer(string text)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 88 ? line : line[..88] + "…";
    }

    private void ShowPlaybackError(string error)
    {
        if (!_closing) _answerStatus.Text = error;
    }

    private void OnRecipientPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (_sessions.Count < 2 || !_recipientSelector.IsEnabled) return;
        _swipeStart = args.GetPosition(_recipientArea);
        _swipePosition = _swipeStart.Value;
        args.Pointer.Capture(_recipientArea);
    }

    private void OnRecipientPointerMoved(object? sender, PointerEventArgs args)
    {
        if (_swipeStart is not null) _swipePosition = args.GetPosition(_recipientArea);
    }

    private void OnRecipientPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (_swipeStart is not Point start) return;
        _swipePosition = args.GetPosition(_recipientArea);
        var deltaX = _swipePosition.X - start.X;
        var deltaY = _swipePosition.Y - start.Y;
        _swipeStart = null;
        args.Pointer.Capture(null);
        if (Math.Abs(deltaX) < 42 || Math.Abs(deltaX) < Math.Abs(deltaY) * 1.25) return;
        SelectOffset(deltaX < 0 ? 1 : -1);
        args.Handled = true;
    }

    private void ChangePhase(Phase phase)
    {
        _phase = phase;
        if (_microphoneState != MicrophoneState.Error)
            _orb.SetState(phase switch
            {
                Phase.Loading or Phase.Finalizing or Phase.PreparingSend or Phase.Sending => VoiceOrbState.Loading,
                Phase.Listening => VoiceOrbState.Listening,
                _ => VoiceOrbState.Idle
            });
        _editor.IsReadOnly = phase != Phase.Editing;
        _editor.Foreground = Brush(phase is Phase.Listening or Phase.Finalizing ? "#CBB4A6" : "#FFF4EFE8");
        _orb.IsEnabled = phase is Phase.Listening or Phase.Editing;
        _recipientSelector.IsEnabled = phase is Phase.Loading or Phase.Listening or Phase.Editing or Phase.StartFailed;
        var canSubmit = phase is Phase.Listening or Phase.Editing;
        _sendButton.IsEnabled = (canSubmit && (_preview || _recipientConfirmed))
            || phase == Phase.StartFailed;
        _confirmRecipientButton.IsEnabled = !_preview && !_recipientConfirmed
            && phase is (Phase.Loading or Phase.Listening or Phase.Editing);
        _sendButton.Content = phase == Phase.StartFailed ? "Повторить"
            : _codexConnection == CodexConnectionStatus.Unavailable
                ? "Проверить вкладку" : "Отправить · остаться";
        AutomationProperties.SetName(_sendButton, phase == Phase.StartFailed
            ? "Повторить запуск микрофона"
            : "Отправить сообщение и оставить экран открытым");
        _closeButton.IsEnabled = phase is not (Phase.Sending or Phase.Closing);
        _hints.Text = phase switch
        {
            Phase.Loading => "Esc — отменить",
            Phase.Finalizing when _backgroundSend is not null =>
                "Enter — закрыть и отправить после уточнения · Esc — отменить",
            Phase.Finalizing => _sendAfterFinalization
                ? "Enter принят · отправлю после уточнения · Esc — отменить"
                : "Enter — отправить после уточнения · Esc — отменить",
            Phase.PreparingSend => "Готовлю автопрослушку · Esc — отменить",
            Phase.Sending => "Отправляю…",
            Phase.StartFailed => "Enter — повторить  ·  Esc — закрыть",
            Phase.Closing => "",
            _ when !_preview && !_recipientConfirmed => "Подтвердите получателя · Esc — отменить",
            _ when _codexConnection == CodexConnectionStatus.Unavailable =>
                "Вкладка Codex изменилась · текст останется для правки",
            _ => "Enter — отправить и закрыть  ·  Esc — отменить"
        };
    }

    private void SetMicrophoneState(MicrophoneState state, string? error = null)
    {
        _microphoneState = state;
        _microphoneError = state == MicrophoneState.Error
            ? string.IsNullOrWhiteSpace(error) ? "Неизвестная ошибка." : error.Trim()
            : "";
        _microphoneStatus.IsVisible = state != MicrophoneState.Inactive;
        _microphoneStatus.Text = state switch
        {
            MicrophoneState.Loading => "Микрофон: запускаю…",
            MicrophoneState.Ready => "Микрофон: запись активна",
            MicrophoneState.Error => $"Микрофон: ошибка — {_microphoneError}",
            _ => ""
        };
        _microphoneStatus.Foreground = Brush(state == MicrophoneState.Error ? "#FFFFA38A" : "#DAC2B5");
        _orb.SetState(state switch
        {
            MicrophoneState.Loading => VoiceOrbState.Loading,
            MicrophoneState.Ready => VoiceOrbState.Listening,
            MicrophoneState.Error => VoiceOrbState.Error,
            _ => VoiceOrbState.Idle
        });
    }

    private void StartPreview()
    {
        ChangePhase(Phase.Listening);
        _orb.SetState(VoiceOrbState.Listening);
        _status.Text = "Слушаю · предпросмотр";
        var tick = 0.0;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _previewTimer.Tick += (_, _) =>
        {
            tick += 0.35;
            _orb.SetLevel((float)(0.3 + 0.32 * Math.Abs(Math.Sin(tick))));
        };
        _previewTimer.Start();
    }

    private async Task StartSpeechAsync()
    {
        if (_closing) return;
        ChangePhase(Phase.Loading);
        SetMicrophoneState(MicrophoneState.Loading);
        _status.Text = "Готовлю распознавание…";
        try { await CleanupTask; }
        catch { /* A previous failed recording must not block a retry. */ }
        if (_closing) return;
        _cleanupStarted = false;
        _playback?.StopForMicrophone();
        try
        {
            await _speech.StartAsync();
            if (_closing) return;
            if (_microphoneState == MicrophoneState.Error)
                throw new InvalidOperationException(_microphoneError);
            SetMicrophoneState(MicrophoneState.Ready);
            ChangePhase(Phase.Listening);
            _status.Text = "Слушаю";
            _micReadyCue.Play();
        }
        catch (Exception exception)
        {
            if (_closing) return;
            try { await _speech.CancelAsync(); }
            catch { /* The startup error is the useful message for the user. */ }
            _playback?.ResumeAfterMicrophone();
            SetMicrophoneState(MicrophoneState.Error,
                _microphoneState == MicrophoneState.Error ? _microphoneError : exception.Message);
            ChangePhase(Phase.Editing);
            _editor.Watermark = "Введите сообщение…";
            _status.Text = "Микрофон не запущен · введите текст или нажмите круг, чтобы повторить";
            FocusEditor();
        }
    }

    private void ShowSpeechStatus(string value)
    {
        if (_closing)
        {
            if (value.StartsWith("Ошибка:", StringComparison.OrdinalIgnoreCase))
                _backgroundSpeechFailure = true;
            return;
        }
        if (value.StartsWith("Ошибка:", StringComparison.OrdinalIgnoreCase)
            && _microphoneState is MicrophoneState.Loading or MicrophoneState.Ready)
        {
            SetMicrophoneState(MicrophoneState.Error, value["Ошибка:".Length..]);
            if (_phase == Phase.Listening)
            {
                ChangePhase(Phase.Editing);
                _editor.Watermark = "Введите сообщение…";
                _status.Text = string.IsNullOrWhiteSpace(_editor.Text)
                    ? "Запись прервана · введите текст или нажмите круг, чтобы повторить"
                    : "Запись прервана · проверьте черновик перед отправкой";
                BeginSpeechCleanup();
                FocusEditor();
            }
            return;
        }
        if (_phase == Phase.Loading)
        {
            if (value.StartsWith("Загрузка Whisper", StringComparison.OrdinalIgnoreCase))
                _status.Text = "Загружаю точную модель…";
            else if (value.StartsWith("Загрузка модели", StringComparison.OrdinalIgnoreCase))
                _status.Text = "Готовлю распознавание…";
            else if (value.StartsWith("Слушаю микрофон", StringComparison.OrdinalIgnoreCase))
                _status.Text = "Проверяю готовность микрофона…";
        }
    }

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(_editor).Properties.IsLeftButtonPressed) return;
        if (_phase == Phase.Editing) return;
        args.Handled = true;
        if (_phase != Phase.Listening) return;

        if (_preview)
        {
            ChangePhase(Phase.Editing);
            _editor.Text = "Проверь тесты, затем объясни причину ошибки простыми словами.";
            _editor.Watermark = "Введите сообщение…";
            _status.Text = "Можно править";
            FocusEditor();
        }
        else _ = FinishRecognitionAsync(sendAfter: false);
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            if (_preview) Close();
            else _ = CancelAsync();
        }
        else if (args.Key == Key.Enter)
        {
            args.Handled = true;
            if (_preview) Close();
            else _ = SubmitAsync(closeAfterSend: true);
        }
    }

    private Task SubmitAsync(bool closeAfterSend)
    {
        if (_closing) return Task.CompletedTask;
        if (closeAfterSend && _backgroundSend is not null
            && _phase is (Phase.Listening or Phase.Finalizing or Phase.Editing))
        {
            BeginBackgroundSend();
            return Task.CompletedTask;
        }
        return _phase switch
        {
            Phase.Listening => FinishRecognitionAsync(sendAfter: true, closeAfterSend),
            Phase.Finalizing => QueueSendAfterFinalization(closeAfterSend),
            Phase.Editing => SendEditedAsync(closeAfterSend),
            Phase.StartFailed => StartSpeechAsync(),
            _ => Task.CompletedTask
        };
    }

    private void BeginBackgroundSend()
    {
        if (_draftStore is null || _backgroundSend is null) return;
        var phase = _phase;
        var selected = SelectedSession;
        var currentText = phase == Phase.Editing ? _editor.Text ?? ""
            : Volatile.Read(ref _latestTranscriptPreview);
        var confirmed = _recipientConfirmed && _confirmedThreadId == selected.ThreadId;
        Func<Task<string>> resolveText = phase switch
        {
            Phase.Listening => () => _speech.FinishAsync(),
            Phase.Finalizing => () => _finalizationTask
                ?? Task.FromException<string>(new InvalidOperationException("Уточнение записи не запущено.")),
            _ => () => Task.FromResult(currentText)
        };
        var draft = VoiceDraft.Capture(_draftOriginThreadId, selected, currentText,
            phase == Phase.Editing ? "Ожидает отправки" : "Ожидает уточнения записи");
        try { _draftStore.Save(draft); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _status.Text = $"Не удалось сохранить черновик: {exception.Message}";
            return;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new BackgroundSendRequest(draft, resolveText,
            () => _backgroundSpeechFailure || _microphoneState == MicrophoneState.Error,
            phase is Phase.Listening or Phase.Finalizing ? () => _speech.CancelAsync() : null,
            confirmed, closed.Task);
        try { CleanupTask = _backgroundSend(request); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            closed.TrySetException(exception);
            _status.Text = $"Не удалось продолжить отправку: {exception.Message}. Черновик сохранён.";
            return;
        }

        _closing = true;
        ChangePhase(Phase.Closing);
        EventHandler? onClosed = null;
        onClosed = (_, _) =>
        {
            Closed -= onClosed;
            try { RestoreTargetFocus(selected); }
            finally { closed.TrySetResult(); }
        };
        Closed += onClosed;
        try { Close(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Closed -= onClosed;
            closed.TrySetException(exception);
            _closing = false;
            ChangePhase(phase);
            _status.Text = $"Не удалось закрыть экран: {exception.Message}. Черновик сохранён.";
            return;
        }
    }

    private Task QueueSendAfterFinalization(bool closeAfterSend)
    {
        _sendAfterFinalization = true;
        _closeAfterFinalization |= closeAfterSend;
        _hints.Text = "Enter принят · отправлю после уточнения · Esc — отменить";
        return Task.CompletedTask;
    }

    private async Task FinishRecognitionAsync(bool sendAfter, bool closeAfterSend = true)
    {
        if (_closing || _phase != Phase.Listening) return;
        _sendAfterFinalization = sendAfter;
        _closeAfterFinalization = sendAfter && closeAfterSend;
        ChangePhase(Phase.Finalizing);
        _orb.SetLevel(0);
        _status.Text = "Завершаю запись…";
        try
        {
            _finalizationTask = _speech.FinishAsync();
            var text = await _finalizationTask;
            _playback?.ResumeAfterMicrophone();
            if (_closing) return;

            _editor.Text = text;
            _editor.Watermark = "Введите сообщение…";
            var microphoneFailed = _microphoneState == MicrophoneState.Error;
            if (!microphoneFailed) SetMicrophoneState(MicrophoneState.Inactive);
            var shouldSend = _sendAfterFinalization;
            var shouldClose = _closeAfterFinalization;
            _sendAfterFinalization = false;
            _closeAfterFinalization = false;
            ChangePhase(Phase.Editing);
            if (shouldSend && !microphoneFailed && !string.IsNullOrWhiteSpace(text))
            {
                await SendEditedAsync(shouldClose);
                return;
            }
            _status.Text = microphoneFailed
                ? "Запись прервана · проверьте черновик перед отправкой"
                : string.IsNullOrWhiteSpace(text)
                ? "Речь не распознана — введите текст"
                : "Можно править";
            FocusEditor();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception exception)
        {
            if (_closing) return;
            _sendAfterFinalization = false;
            _closeAfterFinalization = false;
            try { await _speech.CancelAsync(); }
            catch { /* Keep the finalization error visible. */ }
            _playback?.ResumeAfterMicrophone();
            SetMicrophoneState(MicrophoneState.Error, exception.Message);
            ChangePhase(Phase.Editing);
            _editor.Watermark = "Введите сообщение…";
            _status.Text = $"Не удалось завершить распознавание: {exception.Message}";
            FocusEditor();
        }
    }

    private async Task SendEditedAsync(bool closeAfterSend)
    {
        if (_closing || _phase != Phase.Editing) return;
        var text = _editor.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Text = _microphoneState == MicrophoneState.Error
                ? "Нет текста · введите его или нажмите круг, чтобы повторить запись"
                : "Введите текст перед отправкой";
            FocusEditor();
            return;
        }

        var selected = SelectedSession;
        if (_restoredDraft is not null && _draftStore is not null)
        {
            try { _draftStore.Save(VoiceDraft.Capture(_draftOriginThreadId, selected,
                    text, "Ожидает подтверждения Codex")); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _status.Text = $"Не удалось сохранить правки черновика: {exception.Message}";
                return;
            }
        }
        if (!TryValidateSelectedRecipient(out var validationError))
        {
            _status.Text = validationError;
            FocusEditor();
            return;
        }

        if (_closing) return;
        if (!TryValidateSelectedRecipient(out validationError)
            || SelectedSession.ThreadId != selected.ThreadId)
        {
            ChangePhase(Phase.Editing);
            _status.Text = validationError.Length > 0 ? validationError
                : "Получатель изменился. Текст не отправлен.";
            FocusEditor();
            return;
        }
        if (!CodexRolloutReceiptVerifier.TryCapture(selected.ThreadId,
                out var baseline, out var receiptError) || baseline is null)
        {
            ChangePhase(Phase.Editing);
            _status.Text = receiptError;
            FocusEditor();
            return;
        }
        ChangePhase(Phase.Sending);
        _status.Text = "Проверяю вкладку и ввожу текст…";
        var enterWasInserted = false;
        try
        {
            var result = await ForegroundTerminalSender.SendAsync(selected, text);
            if (_closing) return;
            if (!result.Entered)
            {
                ChangePhase(Phase.Editing);
                _status.Text = result.Message;
                FocusEditor();
                return;
            }
            enterWasInserted = true;
            if (closeAfterSend)
            {
                _closing = true;
                ChangePhase(Phase.Closing);
                Close();
                RestoreTargetFocus(selected);
                CodexDeliveryObserver.Observe(baseline, result.SubmittedText, selected);
                return;
            }
            _status.Text = "Enter передан · проверяю появление сообщения в Codex…";
            var received = await CodexRolloutReceiptVerifier.WaitForUserMessageAsync(
                baseline, result.SubmittedText);
            if (_closing) return;
            if (!received)
            {
                ChangePhase(Phase.Editing);
                _status.Text = "Enter передан, но сообщение не найдено в этой сессии. Проверьте консоль перед повтором.";
                FocusEditor();
                return;
            }
            if (_restoredDraft is not null && _draftStore is not null)
            {
                try { _draftStore.Delete(_draftOriginThreadId); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    ChangePhase(Phase.Editing);
                    _status.Text = $"Codex получил сообщение, но черновик не удалён: {exception.Message}";
                    return;
                }
            }
            _editor.Text = "";
            ChangePhase(Phase.Editing);
            _status.Text = $"Получено Codex → {selected.Project} / {selected.Name}";
            Activate();
            FocusEditor();
        }
        catch (Exception exception)
        {
            if (_closing) return;
            ChangePhase(Phase.Editing);
            _status.Text = enterWasInserted
                ? $"Enter передан, но получение не подтверждено: {exception.Message}"
                : $"Не удалось передать текст: {exception.Message}";
            FocusEditor();
        }
    }

    private void FocusEditor()
    {
        _editor.Focus();
        _editor.CaretIndex = _editor.Text?.Length ?? 0;
    }

    private Task CancelAsync()
    {
        if (_closing || _phase == Phase.Sending) return Task.CompletedTask;
        if (_restoredDraft is not null && _draftStore is not null)
        {
            try
            {
                _draftStore.Save(VoiceDraft.Capture(_draftOriginThreadId, SelectedSession,
                    _editor.Text ?? "", "Сохранён для проверки"));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _status.Text = $"Не удалось сохранить правки черновика: {exception.Message}";
                return Task.CompletedTask;
            }
        }
        var previous = _phase;
        _closing = true;
        ChangePhase(Phase.Closing);
        if (previous is Phase.Loading or Phase.Listening or Phase.Finalizing)
            BeginSpeechCleanup();
        Close();
        RestoreTargetFocus();
        return Task.CompletedTask;
    }

    private void LeaveForAccount(Action? openAccount)
    {
        if (_closing || _phase is Phase.PreparingSend or Phase.Sending || openAccount is null) return;
        var text = _phase == Phase.Editing ? _editor.Text ?? ""
            : Volatile.Read(ref _latestTranscriptPreview);
        if (!string.IsNullOrWhiteSpace(text) && _draftStore is not null)
        {
            try { _draftStore.Save(VoiceDraft.Capture(_draftOriginThreadId, SelectedSession,
                text, "Сохранён для проверки")); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _status.Text = $"Не удалось сохранить черновик: {exception.Message}";
                return;
            }
        }
        var previous = _phase;
        _closing = true;
        _sendAfterFinalization = false;
        ChangePhase(Phase.Closing);
        if (previous is Phase.Loading or Phase.Listening or Phase.Finalizing) BeginSpeechCleanup();
        Close(); // The full-screen topmost voice overlay must not cover the browser.
        openAccount();
    }

    private void BeginSpeechCleanup()
    {
        if (_cleanupStarted || _preview) return;
        _cleanupStarted = true;
        try { _speech.RequestCancel(); }
        catch { /* StopAsync still releases capture resources. */ }
        CleanupTask = Task.Run(async () =>
        {
            try { await _speech.CancelAsync(); }
            catch { /* Cleanup must not reopen a dismissed overlay. */ }
            finally { _playback?.ResumeAfterMicrophone(); }
        });
    }

    private void RestoreTargetFocus(CapturedSession? target = null)
    {
        var window = (target ?? SelectedSession).Window;
        if (window != IntPtr.Zero) SetForegroundWindow(window);
    }

    private void PlaceOverTargetScreen()
    {
        var screen = Screens.Primary;
        if (_captured.Window != IntPtr.Zero && GetWindowRect(_captured.Window, out var rect))
            screen = Screens.ScreenFromPoint(new PixelPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)) ?? screen;
        if (screen is null) return;
        Position = screen.Bounds.Position;
        WindowState = WindowState.FullScreen;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
