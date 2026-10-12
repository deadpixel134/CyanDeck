using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopDeck.Views;

public static class TextPrompt
{
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 6, 0, 14),
            Padding = new Thickness(8, 6, 8, 6),
            MinWidth = 280,
            Background = new SolidColorBrush(Color.FromRgb(38, 41, 48)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 82, 96)),
            CaretBrush = Brushes.White
        };

        var ok = new Button { Content = "확인", MinWidth = 78, Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "취소", MinWidth = 78, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(210, 216, 226)) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Width = 350,
            Height = 155,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(27, 29, 34)),
            Content = panel
        };

        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); window.DialogResult = true; };
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        window.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
