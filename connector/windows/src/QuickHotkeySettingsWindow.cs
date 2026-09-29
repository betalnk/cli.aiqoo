using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexVoice;

internal sealed class QuickHotkeySettingsWindow : Window
{
    private readonly Func<HotkeyGesture, Task<HotkeyChangeResult>> _apply;
    private readonly TextBlock _candidateText;
    private readonly TextBlock _status;
    private readonly Button _save;
    private HotkeyGesture _candidate;

    internal QuickHotkeySettingsWindow(HotkeyGesture current,
        Func<HotkeyGesture, Task<HotkeyChangeResult>> apply)
    {
        _candidate = current;
        _apply = apply;
        Title = "Быстрая голосовая клавиша";
        Width = 390;
        Height = 205;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#FF1E1A18"));

        _candidateText = new TextBlock
        {
            Text = current.Display,
            FontSize = 21,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FFB86D4F")),
            Margin = new Thickness(0, 7, 0, 3)
        };
        _status = new TextBlock
        {
            Text = "Нажмите новое сочетание: два модификатора и букву, цифру, F1–F12 или пробел.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#FFF4EFE8")),
            Height = 36
        };
        _save = new Button { Content = "Сохранить", MinWidth = 100 };
        var cancel = new Button { Content = "Отмена", MinWidth = 82 };
        _save.Click += Save_OnClick;
        cancel.Click += (_, _) => Close();
        Content = new Border
        {
            Padding = new Thickness(18, 14),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Быстрый ввод · удерживать для записи",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.Parse("#FFF4EFE8"))
                    },
                    _candidateText,
                    _status,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 10, 0, 0),
                        Children = { _save, cancel }
                    }
                }
            }
        };
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            Close();
            return;
        }
        if (!HotkeyGesture.TryFromKey(args.Key, args.KeyModifiers, out var candidate)) return;
        args.Handled = true;
        _candidate = candidate;
        _candidateText.Text = candidate.Display;
        _status.Text = "Нажмите «Сохранить», чтобы применить.";
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs args)
    {
        _save.IsEnabled = false;
        var result = await _apply(_candidate);
        if (result.Success) Close();
        else
        {
            _status.Text = result.Error;
            _save.IsEnabled = true;
        }
    }
}
