using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CodexVoice;

public sealed partial class App : Application
{
    private HotkeyListener? _hotkey;
    private VoiceOverlay? _overlay;
    private QuickVoiceWindow? _quickWindow;
    private QuickHotkeySettingsWindow? _quickHotkeySettingsWindow;
    private SessionPlaybackController? _playback;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Task _cleanupTask = Task.CompletedTask;
    private Task _quickCleanupTask = Task.CompletedTask;
    private int _backgroundVoiceSends;
    private int _pairingApprovalPending;
    private CapturedSession? _pendingCapture;
    private QuickRequest? _pendingQuickRequest;
    private bool _openingAfterCleanup;
    private bool _openingQuickAfterCleanup;
    private bool _quickReleasedBeforeOpen;
    private bool _exiting;
    private readonly QuickHotkeySettings _quickSettings = new();
    private readonly VoiceDraftStore _draftStore = new();
    private readonly VoicePairingStore _pairingStore = new();
    private VoicePairingManager? _pairingManager;
    private CliPairingApiClient? _pairingApi;
    private CliDeviceConnection? _deviceConnection;
    private VoicePairingWindow? _pairingWindow;
    private bool _openingPairing;
    private StatusToast? _backgroundProgressToast;
    private HotkeyGesture _quickGesture = HotkeyGesture.DefaultQuick;
    private readonly SemaphoreSlim _versionCheckGate = new(1, 1);
    private VersionCheckService? _versions;
    private CancellationTokenSource? _versionCts;
    private Version? _notifiedVersion;
    private Uri? _availableReleaseUrl;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) =>
            {
                _exiting = true;
                _pendingCapture = null;
                _pendingQuickRequest = null;
                _versionCts?.Cancel();
                _versions?.Dispose();
                _hotkey?.Dispose();
                _playback?.Dispose();
                _deviceConnection?.Dispose();
                _pairingApi?.Dispose();
                _pairingManager?.Dispose();
            };
            if (Program.Preview)
            {
                _overlay = new VoiceOverlay(new CapturedSession(IntPtr.Zero, "", "Предпросмотр интерфейса", "Codex Voice", ""), preview: true);
                _overlay.Closed += (_, _) => desktop.Shutdown();
                _overlay.Show();
            }
            else
            {
                _playback = new SessionPlaybackController();
                _playback.Error += OnPlaybackError;
                _pairingManager = new VoicePairingManager(_pairingStore);
                _pairingApi = new CliPairingApiClient(_pairingStore, _pairingManager);
                _deviceConnection = new CliDeviceConnection(_pairingStore, _pairingManager,
                    message => Dispatcher.UIThread.Post(() =>
                    {
                        if (_exiting) return;
                        _pairingWindow?.SetStatus(message);
                        ShowError(message);
                    }), RemoteInputIdle, approve: ApproveCliPairingAsync);
                _deviceConnection.Start();
                _quickGesture = _quickSettings.Load(out var settingsError);
                _hotkey = new HotkeyListener(OnCapture, OnQuickPress, OnQuickRelease, ShowError,
                    _quickGesture);
                if (settingsError.Length > 0) ShowError(settingsError);
                _versions = new VersionCheckService();
                _versionCts = new CancellationTokenSource();
                _ = MonitorVersionsAsync(_versionCts.Token);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private bool RemoteInputIdle()
    {
        try
        {
            return Dispatcher.UIThread.InvokeAsync(() => !_exiting
                && _overlay is not { IsVisible: true }
                && _quickWindow is null
                && Volatile.Read(ref _backgroundVoiceSends) == 0
                && Volatile.Read(ref _pairingApprovalPending) == 0
                && !_openingAfterCleanup && !_openingQuickAfterCleanup
                && _cleanupTask.IsCompleted && _quickCleanupTask.IsCompleted)
                .GetAwaiter().GetResult();
        }
        catch { return false; }
    }

    private async Task<bool> ApproveCliPairingAsync(VoicePairingApprovalRequest request, CancellationToken token)
    {
        Interlocked.Increment(ref _pairingApprovalPending);
        try { return await VoicePairingApprovalWindow.AskAsync(request, token); }
        finally { Interlocked.Decrement(ref _pairingApprovalPending); }
    }

    private void OnCapture(CapturedSession target)
    {
        if (_exiting || _quickWindow is not null || _openingQuickAfterCleanup) return;
        if (_overlay is { IsVisible: true }) return;
        if (_openingAfterCleanup || !_cleanupTask.IsCompleted || !_quickCleanupTask.IsCompleted)
        {
            _pendingCapture = target;
            if (!_openingAfterCleanup)
            {
                _openingAfterCleanup = true;
                _ = OpenWhenReadyAsync();
            }
            return;
        }
        if (!SessionResolver.TryValidate(target, out var validationError))
        {
            ShowError(validationError);
            return;
        }
        ShowOverlay(target);
    }

    private async Task OpenWhenReadyAsync()
    {
        try { await Task.WhenAll(_cleanupTask, _quickCleanupTask); }
        catch { /* A failed cleanup must not block a new capture forever. */ }
        _openingAfterCleanup = false;
        var target = _pendingCapture;
        _pendingCapture = null;
        if (_exiting || target is null || _overlay is { IsVisible: true }) return;
        if (!SessionResolver.TryValidate(target, out var error))
        {
            ShowError(error);
            return;
        }
        ShowOverlay(target);
    }

    private void OnQuickPress(IntPtr foregroundWindow)
    {
        if (_exiting || _overlay is { IsVisible: true } || _openingAfterCleanup
            || _quickWindow is not null || _openingQuickAfterCleanup) return;

        SessionResolver.TryCaptureQuickTarget(foregroundWindow, out var target, out var error);
        var request = new QuickRequest(foregroundWindow, target, error);
        _quickReleasedBeforeOpen = false;
        if (!_cleanupTask.IsCompleted || !_quickCleanupTask.IsCompleted)
        {
            _pendingQuickRequest = request;
            _openingQuickAfterCleanup = true;
            _ = OpenQuickWhenReadyAsync();
            return;
        }
        ShowQuick(request);
    }

    private void OnQuickRelease()
    {
        if (_quickWindow is { } quick) quick.OnHotkeyReleased();
        else if (_openingQuickAfterCleanup) _quickReleasedBeforeOpen = true;
    }

    private async Task OpenQuickWhenReadyAsync()
    {
        try { await Task.WhenAll(_cleanupTask, _quickCleanupTask); }
        catch { /* A cleanup failure does not choose a different recipient. */ }
        _openingQuickAfterCleanup = false;
        var request = _pendingQuickRequest;
        _pendingQuickRequest = null;
        if (_exiting || request is null || _overlay is { IsVisible: true }) return;
        ShowQuick(request);
    }

    private void ShowQuick(QuickRequest request)
    {
        var quick = new QuickVoiceWindow(request.Target, request.Error, request.ForegroundWindow);
        _quickWindow = quick;
        quick.Closed += (_, _) =>
        {
            _quickCleanupTask = quick.CleanupTask;
            if (ReferenceEquals(_quickWindow, quick)) _quickWindow = null;
        };
        quick.Show();
        if (_quickReleasedBeforeOpen)
        {
            _quickReleasedBeforeOpen = false;
            quick.OnHotkeyReleased();
        }
    }

    private async Task<HotkeyChangeResult> ApplyQuickHotkeyAsync(HotkeyGesture gesture)
    {
        if (_hotkey is null) return new(false, "Обработчик горячих клавиш недоступен.");
        var previous = _quickGesture;
        var change = await _hotkey.ChangeQuickHotkeyAsync(gesture);
        if (!change.Success) return change;
        try
        {
            _quickSettings.Save(gesture);
            _quickGesture = gesture;
            return new(true, "");
        }
        catch (Exception exception)
        {
            await _hotkey.ChangeQuickHotkeyAsync(previous);
            return new(false, $"Не удалось сохранить настройку: {exception.Message}");
        }
    }

    private void QuickHotkeySettings_OnClick(object? sender, EventArgs args)
        => OpenQuickHotkeySettings();

    private void OpenQuickHotkeySettings()
    {
        if (_quickHotkeySettingsWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }
        var window = new QuickHotkeySettingsWindow(_quickGesture, ApplyQuickHotkeyAsync);
        window.Topmost = _overlay is { IsVisible: true };
        _quickHotkeySettingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_quickHotkeySettingsWindow, window)) _quickHotkeySettingsWindow = null;
        };
        if (_overlay is { IsVisible: true } overlay) window.Show(overlay);
        else window.Show();
        window.Activate();
    }

    private sealed record QuickRequest(IntPtr ForegroundWindow, QuickTarget? Target, string Error);

    private void ShowOverlay(CapturedSession target)
    {
        // A hotkey always starts a new recording for the captured exact session.
        // Keep an earlier uncertain message on disk without restoring its text,
        // recipient or confirmation state into this activation.
        try { _draftStore.PreserveForFreshCapture(target.ThreadId); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowError($"Не удалось сохранить предыдущий черновик: {exception.Message}");
            return;
        }
        var overlay = new VoiceOverlay(target, _playback,
            openHotkeySettings: OpenQuickHotkeySettings, openCliAccount: OpenCliAccount,
            draftStore: _draftStore, backgroundSend: SendAfterOverlayClosesAsync);
        _overlay = overlay;
        overlay.Closed += (_, _) =>
        {
            _cleanupTask = overlay.CleanupTask;
            if (ReferenceEquals(_overlay, overlay)) _overlay = null;
            if (!overlay.WantsAutoRead) _playback?.StopIdleMonitoring();
        };
        overlay.Show();
        overlay.Activate();
    }

    private async void OpenCliAccount()
    {
        if (_exiting || _openingPairing) return;
        _openingPairing = true;
        try
        {
            ShowError("Открываю личный кабинет CLI…");
            var offer = await StartCliPairingAsync();
            if (_exiting) return;
            var fragment = new Uri(offer.QrUrl).Fragment;
            Process.Start(new ProcessStartInfo("https://cli.aiqoo.ru/account.html" + fragment)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            ShowError("Не удалось открыть привязку CLI. Проверьте соединение и повторите.");
        }
        finally { _openingPairing = false; }
    }

    private void OpenCliAccount_OnClick(object? sender, EventArgs args) => OpenCliAccount();

    private async void Pairing_OnClick(object? sender, EventArgs args)
        => await OpenPairingWindowAsync();

    private async Task<VoicePairingOffer> StartCliPairingAsync()
    {
        var offer = await (_pairingApi?.StartAsync()
            ?? Task.FromException<VoicePairingOffer>(new InvalidOperationException("CLI pairing is unavailable.")));
        _deviceConnection?.Start();
        return offer;
    }

    private async Task OpenPairingWindowAsync()
    {
        if (_exiting || _openingPairing) return;
        if (_pairingWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }
        _openingPairing = true;
        try
        {
            ShowError("Создаю код подключения CLI…");
            var offer = await StartCliPairingAsync();
            if (_exiting) return;
            var window = new VoicePairingWindow(offer, StartCliPairingAsync);
            _pairingWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_pairingWindow, window)) _pairingWindow = null;
            };
            window.Show();
            window.Activate();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowError($"Не удалось создать код CLI: {exception.Message}");
        }
        finally { _openingPairing = false; }
    }

    private async Task SendAfterOverlayClosesAsync(BackgroundSendRequest request)
    {
        Interlocked.Increment(ref _backgroundVoiceSends);
        var draft = request.Draft;
        var codexReceiptConfirmed = false;
        try
        {
            // A terminal can only be focused after the voice window has disappeared.
            await request.OverlayClosed;
            if (request.CancelSpeechOnFailure is not null)
                ShowBackgroundProgress("Уточняю запись на ПК · черновик сохранён");
            string text;
            try { text = await request.ResolveText(); }
            catch
            {
                if (request.CancelSpeechOnFailure is not null)
                {
                    try { await request.CancelSpeechOnFailure(); }
                    catch { /* The original finalization error is more useful. */ }
                }
                throw;
            }
            draft = draft.Update(text, "Распознано; отправка ещё не подтверждена");
            try { _draftStore.Save(draft); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                NotifyBackground($"Итоговый текст не удалось сохранить: {exception.Message}. Начальный черновик сохранён; отправка остановлена.");
                return;
            }

            if (_exiting) return;

            if (request.HasMicrophoneFailure())
            {
                KeepBackgroundDraft(draft, "Ошибка микрофона или распознавания",
                    "Распознавание прервалось. Черновик сохранён; откройте голосовой экран снова.");
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                KeepBackgroundDraft(draft, "Речь не распознана",
                    "Речь не распознана. Черновик сохранён; откройте голосовой экран снова.");
                return;
            }
            if (!request.RecipientConfirmed)
            {
                KeepBackgroundDraft(draft, "Получатель не подтверждён",
                    "Получатель не подтверждён. Черновик сохранён; проверьте сессию.");
                return;
            }

            var selected = draft.Recipient.ToCapturedSession();
            if (!SessionResolver.TryValidate(selected, out var targetError))
            {
                KeepBackgroundDraft(draft, targetError,
                    "Сессия Codex изменилась. Не отправлено; черновик сохранён.");
                return;
            }
            if (!CodexRolloutReceiptVerifier.TryCapture(selected.ThreadId,
                    out var baseline, out var receiptError) || baseline is null)
            {
                KeepBackgroundDraft(draft, receiptError,
                    "Не удалось проверить журнал Codex. Не отправлено; черновик сохранён.");
                return;
            }

            ShowBackgroundProgress("Передаю текст в выбранную вкладку Codex · черновик сохранён");
            var result = await ForegroundTerminalSender.SendAsync(selected, text);
            if (!result.Entered)
            {
                KeepBackgroundDraft(draft, result.Message,
                    result.Message + " Черновик сохранён; проверьте терминал перед повтором.");
                return;
            }

            var received = await CodexRolloutReceiptVerifier.WaitForUserMessageAsync(
                baseline, result.SubmittedText);
            if (!received)
            {
                KeepBackgroundDraft(draft, "Получение Codex неизвестно",
                    "Приём Codex не подтверждён. Проверьте вкладку перед повтором; черновик сохранён.");
                return;
            }

            codexReceiptConfirmed = true;
            _draftStore.Delete(draft.OriginalThreadId);
            NotifyBackground($"Получено Codex → {selected.Project} / {selected.Name}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (codexReceiptConfirmed)
            {
                try { _draftStore.Save(draft.Update(draft.Text,
                    "Codex уже получил сообщение; удалить черновик не удалось")); }
                catch { /* The receipt still proves delivery; never resend automatically. */ }
                NotifyBackground($"Codex получил сообщение, но черновик не удалён: {exception.Message}. Не отправляйте его повторно.");
                return;
            }
            KeepBackgroundDraft(draft, "Ошибка завершения или отправки",
                $"Ошибка записи или отправки: {exception.Message}. Черновик сохранён.");
        }
        finally
        {
            _playback?.ResumeAfterMicrophone();
            CloseBackgroundProgress();
            Interlocked.Decrement(ref _backgroundVoiceSends);
        }
    }

    private void KeepBackgroundDraft(VoiceDraft draft, string status, string notification)
    {
        try { _draftStore.Save(draft.Update(draft.Text, status)); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotifyBackground(notification + $" Статус файла не обновлён: {exception.Message}");
            return;
        }
        NotifyBackground(notification);
    }

    private void NotifyBackground(string message)
    {
        if (_exiting) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_exiting) return;
            _backgroundProgressToast?.Close();
            _backgroundProgressToast = null;
            ShowError(message);
        });
    }

    private void ShowBackgroundProgress(string message) => Dispatcher.UIThread.Post(() =>
    {
        if (_exiting) return;
        _backgroundProgressToast?.Close();
        var toast = new StatusToast(message, persistent: true);
        _backgroundProgressToast = toast;
        toast.Show();
    });

    private void CloseBackgroundProgress() => Dispatcher.UIThread.Post(() =>
    {
        _backgroundProgressToast?.Close();
        _backgroundProgressToast = null;
    });

    private void ShowError(string error)
    {
        var toast = new StatusToast(error);
        toast.Show();
    }

    private void OnPlaybackError(string error)
    {
        if (_overlay is not { IsVisible: true }) ShowError(error);
    }

    private void StopPlayback_OnClick(object? sender, EventArgs args)
    {
        if (_playback is null) return;
        _playback.AutoRead = false;
        _playback.StopPlayback();
        if (_overlay is not { IsVisible: true }) _playback.StopIdleMonitoring();
    }

    private async Task MonitorVersionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckVersionAsync(manual: false, cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(20));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await CheckVersionAsync(manual: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { /* Version checking must never stop microphone capture or the hotkey. */ }
    }

    private async Task CheckVersionAsync(bool manual, CancellationToken cancellationToken)
    {
        if (_versions is null || _exiting) return;
        VersionCheckResult result;
        await _versionCheckGate.WaitAsync(cancellationToken);
        try { result = await _versions.CheckAsync(cancellationToken); }
        finally { _versionCheckGate.Release(); }
        if (_exiting) return;

        if (result.State == VersionCheckState.Available &&
            result.RemoteVersion is { } version && result.ReleaseUrl is { } url)
        {
            _availableReleaseUrl = url;
            if (manual || _notifiedVersion is null || version > _notifiedVersion)
                new StatusToast($"Доступна версия {version} · открыть: трей → Версии на GitHub").Show();
            _notifiedVersion = version;
            return;
        }
        if (!manual) return;

        var message = result.State switch
        {
            VersionCheckState.Current => $"Версия {VersionCheckService.CurrentVersion} актуальна.",
            VersionCheckState.NotPublished when result.RemoteVersion is { } pendingVersion =>
                $"Версия {pendingVersion} ещё не опубликована.",
            VersionCheckState.NotPublished => "Пока нет опубликованного обновления.",
            _ => "Не удалось проверить версию. Повторите позже."
        };
        new StatusToast(message).Show();
    }

    private async void CheckVersion_OnClick(object? sender, EventArgs args)
    {
        try { await CheckVersionAsync(manual: true, _versionCts?.Token ?? CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch { if (!_exiting) new StatusToast("Не удалось проверить версию.").Show(); }
    }

    private void OpenReleases_OnClick(object? sender, EventArgs args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_availableReleaseUrl?.AbsoluteUri ??
                VersionCheckService.ReleasesPage.AbsoluteUri) { UseShellExecute = true });
        }
        catch { if (!_exiting) new StatusToast("Не удалось открыть страницу выпусков.").Show(); }
    }

    private void Exit_OnClick(object? sender, EventArgs args) => _desktop?.Shutdown();
}
