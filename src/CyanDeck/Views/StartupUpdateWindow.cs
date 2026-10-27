using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CyanDeck.Views;

/// <summary>Non-activating startup status, shown before any desktop-band window.</summary>
public sealed class StartupUpdateWindow : Window
{
    private readonly TextBlock _status;
    private readonly Button _skip;

    public event Action? SkipRequested;

    public StartupUpdateWindow()
    {
        Title = "CyanDeck 업데이트";
        Width = 460;
        Height = 242;
        MinWidth = 460;
        MaxWidth = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // The only user-dismissal path is the explicit Skip action, which cancels
        // the current network/download operation before opening the desktop.
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = true;
        ShowActivated = false;
        Background = new SolidColorBrush(Color.FromRgb(26, 30, 38));
        Foreground = Brushes.White;

        var layout = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        layout.Children.Add(new TextBlock
        {
            Text = "CyanDeck 시작 준비",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 11)
        });
        _status = new TextBlock
        {
            Text = "업데이트 확인 중...",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(191, 202, 217)),
            Margin = new Thickness(0, 0, 0, 13)
        };
        layout.Children.Add(_status);
        layout.Children.Add(new ProgressBar
        {
            IsIndeterminate = true,
            Height = 7,
            Foreground = new SolidColorBrush(Color.FromRgb(53, 192, 215)),
            Background = new SolidColorBrush(Color.FromRgb(57, 64, 76)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        layout.Children.Add(new TextBlock
        {
            Text = "확인 및 설치 준비가 끝나면 그룹과 캘린더가 표시됩니다.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(151, 164, 183)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 13)
        });
        _skip = new Button
        {
            Content = "이번에는 건너뛰고 실행",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 5, 14, 5)
        };
        // Dark template: the stock one turned the button light blue on hover and bright grey
        // once it disables itself after the click.
        if (DeckThemeResources.ControlStyle("DarkButtonStyle") is { } skipStyle)
            _skip.Style = skipStyle;
        else
        {
            _skip.Background = new SolidColorBrush(Color.FromRgb(45, 54, 67));
            _skip.Foreground = Brushes.White;
        }
        _skip.Click += (_, _) =>
        {
            _skip.IsEnabled = false;
            SkipRequested?.Invoke();
        };
        layout.Children.Add(_skip);
        Content = layout;
    }

    public void SetStage(string message, bool allowSkip)
    {
        _status.Text = message.Replace("\\n", Environment.NewLine);
        _skip.IsEnabled = allowSkip;
        _skip.Visibility = allowSkip ? Visibility.Visible : Visibility.Collapsed;
    }
}
