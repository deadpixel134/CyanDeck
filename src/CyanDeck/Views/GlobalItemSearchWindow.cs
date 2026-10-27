using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CyanDeck.Services;

namespace CyanDeck.Views;

/// <summary>
/// Dark "항목 찾기" window: searches the names of icons in every group, tab and page, the
/// current folder of every Folder Portal and the icons outside groups. Enter opens the
/// selected item; Shift+Enter or "위치로 이동" shows it in its group. The window closes
/// before either action so it never covers the result. High contrast keeps system colours.
/// </summary>
internal sealed class GlobalItemSearchWindow : Window
{
    private readonly List<GlobalItemSearchEntry> _entries;
    private readonly Action<GlobalItemSearchEntry> _open;
    private readonly Action<GlobalItemSearchEntry> _reveal;
    private readonly TextBox _query;
    private readonly ListBox _results;
    private readonly TextBlock _status;
    private readonly Button _openButton;
    private readonly Button _revealButton;
    private readonly DispatcherTimer _debounce;
    private readonly bool _highContrast = SystemParameters.HighContrast;
    private bool _portalsLoading;
    private bool _actionStarted;

    internal GlobalItemSearchWindow(
        IEnumerable<GlobalItemSearchEntry> entries,
        Action<GlobalItemSearchEntry> open,
        Action<GlobalItemSearchEntry> reveal)
    {
        _entries = entries.ToList();
        _open = open;
        _reveal = reveal;

        Title = "항목 찾기";
        Width = 560;
        Height = 480;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.CanResize;
        if (!_highContrast)
        {
            Background = new SolidColorBrush(Color.FromRgb(27, 29, 34));
            Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248));
            DeckThemeResources.MergeDarkControls(Resources);
        }

        _query = new TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 6)
        };
        System.Windows.Automation.AutomationProperties.SetName(_query, "검색할 이름");

        _status = new TextBlock
        {
            Margin = new Thickness(2, 0, 0, 8),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        if (!_highContrast) _status.Foreground = new SolidColorBrush(Color.FromRgb(170, 180, 194));

        _results = new ListBox
        {
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        if (!_highContrast)
        {
            _results.Background = new SolidColorBrush(Color.FromRgb(32, 36, 43));
            _results.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 66, 78));
            _results.Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248));
            _results.ItemContainerStyle = CreateResultItemStyle();
        }

        _revealButton = new Button { Content = "위치로 이동", MinWidth = 96, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        _openButton = new Button { Content = "열기", MinWidth = 78, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        var close = new Button { Content = "닫기", MinWidth = 78, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        if (!_highContrast && DeckThemeResources.ControlStyle("DeckPrimaryButtonStyle") is { } primary)
            _openButton.Style = primary;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(close);
        buttons.Children.Add(_revealButton);
        buttons.Children.Add(_openButton);

        var label = new TextBlock { Text = "모든 그룹·탭·페이지와 그룹 밖 아이콘에서 이름으로 찾습니다.", TextWrapping = TextWrapping.Wrap };
        if (!_highContrast) label.Foreground = new SolidColorBrush(Color.FromRgb(210, 216, 226));

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(label, 0);
        Grid.SetRow(_query, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(_results, 3);
        Grid.SetRow(buttons, 4);
        root.Children.Add(label);
        root.Children.Add(_query);
        root.Children.Add(_status);
        root.Children.Add(_results);
        root.Children.Add(buttons);
        Content = root;

        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(140) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RunSearch();
        };

        _query.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _query.PreviewKeyDown += Query_PreviewKeyDown;
        _results.MouseDoubleClick += (_, e) =>
        {
            if (SelectedEntry() is null) return;
            e.Handled = true;
            Run(reveal: false);
        };
        _results.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Run(reveal: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        };
        _results.SelectionChanged += (_, _) => UpdateButtons();
        _openButton.Click += (_, _) => Run(reveal: false);
        _revealButton.Click += (_, _) => Run(reveal: true);
        close.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) => _debounce.Stop();

        RunSearch();
    }

    internal void SetPortalsLoading(bool loading)
    {
        _portalsLoading = loading;
        UpdateStatus(_results.Items.Count);
    }

    internal void AddEntries(IEnumerable<GlobalItemSearchEntry> entries)
    {
        _entries.AddRange(entries);
        _portalsLoading = false;
        RunSearch();
    }

    internal void FocusQuery()
    {
        _query.Focus();
        _query.SelectAll();
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                // Pending typing is searched first so Enter always acts on what was typed.
                if (_debounce.IsEnabled)
                {
                    _debounce.Stop();
                    RunSearch();
                }
                Run(reveal: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = _results.Items.Count;
        if (count == 0) return;
        var index = Math.Clamp(_results.SelectedIndex + delta, 0, count - 1);
        _results.SelectedIndex = index;
        _results.ScrollIntoView(_results.Items[index]);
    }

    private void RunSearch()
    {
        var found = GlobalItemSearchPolicy.Search(_entries, _query.Text);
        _results.Items.Clear();
        foreach (var entry in found) _results.Items.Add(CreateRow(entry));
        if (found.Count > 0) _results.SelectedIndex = 0;
        UpdateStatus(found.Count);
        UpdateButtons();
    }

    private void UpdateStatus(int shown)
    {
        string text;
        if (GlobalItemSearchPolicy.Tokenize(_query.Text).Length == 0)
            text = "이름의 일부를 입력하세요. Enter: 열기 · Shift+Enter: 위치로 이동 · Esc: 닫기";
        else if (shown == 0)
            text = "일치하는 항목이 없습니다.";
        else if (shown >= GlobalItemSearchPolicy.MaxResults)
            text = $"{shown}개 이상 — 처음 {GlobalItemSearchPolicy.MaxResults}개만 표시합니다. 더 자세히 입력하세요.";
        else
            text = $"{shown}개 찾음";
        if (_portalsLoading) text += " · 폴더 포털을 확인하는 중…";
        _status.Text = text;
    }

    private void UpdateButtons()
    {
        var entry = SelectedEntry();
        _openButton.IsEnabled = entry is not null;
        _revealButton.IsEnabled = entry?.CanReveal == true;
        _revealButton.ToolTip = entry is { CanReveal: false } ? "그룹 밖 아이콘은 위치로 이동할 수 없습니다." : null;
    }

    private GlobalItemSearchEntry? SelectedEntry()
        => (_results.SelectedItem as ListBoxItem)?.Tag as GlobalItemSearchEntry;

    private void Run(bool reveal)
    {
        if (_actionStarted) return;
        var entry = SelectedEntry();
        if (entry is null || (reveal && !entry.CanReveal)) return;
        _actionStarted = true;
        var action = reveal ? _reveal : _open;
        var dispatcher = Dispatcher;
        Close();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => action(entry)));
    }

    private ListBoxItem CreateRow(GlobalItemSearchEntry entry)
    {
        var name = new TextBlock
        {
            Text = entry.DisplayName,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var location = new TextBlock
        {
            Text = entry.Location,
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (!_highContrast)
        {
            name.Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248));
            location.Foreground = new SolidColorBrush(Color.FromRgb(160, 172, 188));
        }
        var panel = new StackPanel();
        panel.Children.Add(name);
        panel.Children.Add(location);
        return new ListBoxItem { Content = panel, Tag = entry, ToolTip = entry.Path };
    }

    // Dark rows: the stock template shows a light grey selection while the search box has focus.
    private static Style CreateResultItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(42, 48, 57))));
        template.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(49, 92, 140))));
        template.Triggers.Add(selected);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
