using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexVoice;

internal sealed class StatusToast : Window
{
    private readonly DispatcherTimer _timer;
    private readonly bool _persistent;

    public StatusToast(string message, bool persistent = false)
    {
        _persistent = persistent;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Width = 420;
        Height = 92;
        Background = Brushes.Transparent;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21E1B1A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#96604A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 13),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "CODEX VOICE", FontSize = 11, FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FFB777")) },
                    new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.White }
                }
            }
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };
        Opened += (_, _) =>
        {
            var screen = Screens.Primary;
            if (screen is not null)
                Position = new PixelPoint(screen.WorkingArea.Right - (int)(Width * screen.Scaling) - 24,
                    screen.WorkingArea.Y + 24);
            if (!_persistent) _timer.Start();
        };
        PointerPressed += (_, _) => Close();
        Closed += (_, _) => _timer.Stop();
    }
}
