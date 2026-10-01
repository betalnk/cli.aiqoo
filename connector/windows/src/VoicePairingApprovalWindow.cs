using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexVoice;

internal sealed class VoicePairingApprovalWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<bool> Answer => _answer.Task;

    internal VoicePairingApprovalWindow(VoicePairingApprovalRequest request)
    {
        Title = "CLI · Подтверждение привязки";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush.Parse("#181819");
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var muted = Brush.Parse("#A7A4A0");
        TextBlock Text(string value, int size = 14, IBrush? color = null) => new()
        {
            Text = value, FontSize = size, Foreground = color ?? Brushes.White,
            TextWrapping = TextWrapping.Wrap
        };
        var cancel = new Button { Content = "Отмена", MinWidth = 100,
            Foreground = Brushes.White, Background = Brush.Parse("#302A28") };
        var ok = new Button
        {
            Content = "ОК", MinWidth = 100, Background = Brush.Parse("#F0A184"),
            Foreground = Brush.Parse("#181819")
        };
        cancel.Click += (_, _) => Finish(false);
        ok.Click += (_, _) => Finish(DateTimeOffset.UtcNow < request.ExpiresAt);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 17,
            Children =
            {
                Text("Разрешить привязку этого ПК?", 23),
                Text(request.AccountEmail.Length > 0 ? request.AccountEmail : "Аккаунт CLI", 17),
                Text($"Браузер: {request.ClientName}\nКомпьютер: {Environment.MachineName}", 13, muted),
                Text("После подтверждения этот браузер сможет читать доступные сессии Codex и отправлять в них сообщения через ваш ПК.", 14, muted),
                Text("Подтвердите, если вы нажали «Привязать» в своём личном кабинете.", 14),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } }
            }
        };
        Opened += (_, _) => cancel.Focus();
        Closed += (_, _) => _answer.TrySetResult(false);
    }

    private void Finish(bool approved)
    {
        _answer.TrySetResult(approved);
        Close();
    }

    internal static async Task<bool> AskAsync(VoicePairingApprovalRequest request, CancellationToken token)
    {
        VoicePairingApprovalWindow? window = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                window = new(request);
                window.Show();
                window.Activate();
            });
            using var cancelled = token.Register(() => Dispatcher.UIThread.Post(() => window?.Finish(false)));
            var approved = await window!.Answer;
            return approved && !token.IsCancellationRequested;
        }
        finally
        {
            if (window is not null) await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (window.IsVisible) window.Close();
            });
        }
    }
}
