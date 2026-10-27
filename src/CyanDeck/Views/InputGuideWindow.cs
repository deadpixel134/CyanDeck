using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyanDeck.Services;

namespace CyanDeck.Views;

/// <summary>
/// v8.0.43 "단축키·제스처 안내": a read-only overview of conveniences that already exist
/// (rectangle creation, Alt+folder drop, page gestures, quick-hide double-click, group
/// keys). The only action creates the optional command shortcuts. Content comes from
/// InputGuideContent so wording stays in one place. Uses system colors in high contrast.
/// </summary>
public sealed class InputGuideWindow : Window
{
    private InputGuideWindow()
    {
        Title = "CyanDeck 단축키·제스처 안내";
        Width = 640;
        Height = 620;
        MinWidth = 460;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var highContrast = SystemParameters.HighContrast;
        Brush background = highContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(28, 34, 45));
        Brush foreground = highContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFD));
        Brush muted = highContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(0xAA, 0xB9, 0xC9));
        Brush card = highContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x31));
        Brush cardBorder = highContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(0x39, 0x43, 0x52));
        Background = background;
        Foreground = foreground;
        if (!highContrast) DeckThemeResources.MergeDarkControls(Resources);

        var root = new DockPanel();
        Content = root;

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(22, 8, 22, 16),
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var close = new Button { Content = "닫기", MinWidth = 100, IsCancel = true, Padding = new Thickness(12, 6, 12, 6) };
        if (!highContrast && DeckThemeResources.ControlStyle("DarkButtonStyle") is { } darkButton) close.Style = darkButton;
        close.Click += (_, _) => Close();
        footer.Children.Add(close);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        scroll.Content = page;

        page.Children.Add(new TextBlock
        {
            Text = "단축키·제스처 안내",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            Margin = new Thickness(0, 0, 0, 6),
        });
        page.Children.Add(new TextBlock
        {
            Text = "CyanDeck에 이미 있는 편리한 조작을 모았습니다. 트레이 → 도구 및 도움말에서 언제든 다시 열 수 있습니다.",
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var sections = InputGuideContent.Sections;
        for (var index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = foreground,
                Margin = new Thickness(0, 0, 0, 8),
            });
            foreach (var row in section.Rows) body.Children.Add(CreateRow(row, foreground, muted));
            if (index == sections.Count - 1)
            {
                var create = new Button
                {
                    Content = "단축키용 바로 가기 만들기…",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(14, 7, 14, 7),
                    Margin = new Thickness(0, 10, 0, 0),
                    ToolTip = "시작 메뉴에 Peek·빠른 숨김·바탕화면 보기·페이지 이동 바로 가기를 만듭니다.",
                };
                if (!highContrast && DeckThemeResources.ControlStyle("DeckPrimaryButtonStyle") is { } primary) create.Style = primary;
                create.Click += (_, _) => CommandShortcutService.CreateInteractive(this);
                body.Children.Add(create);
            }

            page.Children.Add(new Border
            {
                Background = card,
                BorderBrush = cardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 0, 0, 14),
                Child = body,
            });
        }
    }

    private static FrameworkElement CreateRow(InputGuideRow row, Brush foreground, Brush muted)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gesture = new TextBlock
        {
            Text = row.Gesture,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var description = new TextBlock
        {
            Text = row.Description,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(gesture, 0);
        Grid.SetColumn(description, 1);
        grid.Children.Add(gesture);
        grid.Children.Add(description);
        return grid;
    }

    /// <summary>Shows the guide (one instance). Non-modal so it can stay open while trying gestures.</summary>
    internal static void ShowGuide(Window? owner)
    {
        var existing = Application.Current?.Windows.OfType<InputGuideWindow>().FirstOrDefault();
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new InputGuideWindow();
        if (owner is not null && owner.IsLoaded && owner.IsVisible)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        window.Show();
        window.Activate();
    }
}
