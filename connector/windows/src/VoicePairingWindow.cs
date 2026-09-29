using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using QRCoder;

namespace CodexVoice;

/// <summary>Local QR display. The fragment secret never goes to a QR web service.</summary>
internal sealed class VoicePairingWindow : Window
{
    private static readonly IBrush BackgroundBrush = Brush.Parse("#181819");
    private static readonly IBrush MutedBrush = Brush.Parse("#A7A4A0");
    private static readonly IBrush AccentBrush = Brush.Parse("#F0A184");
    private readonly Func<Task<VoicePairingOffer>> _refresh;
    private readonly Image _qr = new() { Width = 310, Height = 310, Stretch = Stretch.Uniform };
    private readonly TextBlock _code = new() { FontSize = 24, FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _remaining = new() { FontSize = 12, Foreground = MutedBrush,
        HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = MutedBrush,
        TextWrapping = TextWrapping.Wrap };
    private readonly Button _refreshButton = new() { Content = "Новый код", IsEnabled = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Bitmap? _bitmap;
    private VoicePairingOffer _offer;

    internal VoicePairingWindow(VoicePairingOffer offer, Func<Task<VoicePairingOffer>> refresh)
    {
        _offer = offer;
        _refresh = refresh;
        Title = "CLI · Подключить телефон";
        Width = 450;
        Height = 650;
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = BackgroundBrush;

        var title = new TextBlock
        {
            Text = "Подключить телефон",
            FontSize = 25,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        };
        var intro = new TextBlock
        {
            Text = "Наведите камеру телефона на QR-код. Войдите в отдельный аккаунт CLI и дождитесь подтверждения на этом ПК.",
            FontSize = 13,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19
        };
        var qrSurface = new Border
        {
            Child = _qr,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(13),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var codeCaption = new TextBlock
        {
            Text = "Код подключения",
            FontSize = 11,
            Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var copyButton = new Button { Content = "Скопировать ссылку" };
        copyButton.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) throw new InvalidOperationException("Clipboard unavailable.");
                await clipboard.SetTextAsync(_offer.QrUrl);
                SetStatus("Ссылка скопирована. Откройте её на телефоне.");
            }
            catch { SetStatus("Не удалось скопировать ссылку."); }
        };
        _refreshButton.Click += async (_, _) =>
        {
            _refreshButton.IsEnabled = false;
            SetStatus("Создаём новый код…");
            try { SetOffer(await _refresh()); SetStatus(""); }
            catch { SetStatus("Не удалось получить новый код. Проверьте соединение и повторите."); _refreshButton.IsEnabled = true; }
        };
        var manageButton = new Button { Content = "Управлять доступом" };
        manageButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://cli.aiqoo.ru/account.html") { UseShellExecute = true });
            }
            catch { SetStatus("Откройте cli.aiqoo.ru/account.html для управления доступом."); }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
            Children = { copyButton, _refreshButton }
        };
        var stack = new StackPanel
        {
            Margin = new Thickness(29, 27),
            Spacing = 11,
            Children =
            {
                title, intro, qrSurface, codeCaption, _code, _remaining,
                actions, _status, manageButton
            }
        };
        Content = stack;
        SetOffer(offer);
        _timer.Tick += (_, _) => RefreshRemaining();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _bitmap?.Dispose();
        };
    }

    internal void SetStatus(string status) => _status.Text = status;

    private void SetOffer(VoicePairingOffer offer)
    {
        _offer = offer;
        var png = PngByteQRCodeHelper.GetQRCode(offer.QrUrl, QRCodeGenerator.ECCLevel.M, 8);
        using var stream = new MemoryStream(png);
        var bitmap = new Bitmap(stream);
        _qr.Source = bitmap;
        _bitmap?.Dispose();
        _bitmap = bitmap;
        _code.Text = offer.DisplayCode;
        RefreshRemaining();
    }

    private void RefreshRemaining()
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_offer.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
        _remaining.Text = seconds > 0
            ? $"Код действует ещё {seconds / 60:00}:{seconds % 60:00}"
            : "Срок действия кода истёк. Создайте новый.";
        _refreshButton.IsEnabled = seconds == 0;
        _code.Foreground = seconds > 0 ? AccentBrush : MutedBrush;
    }
}
