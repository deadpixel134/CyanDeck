using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CyanDeck.Calendar;

/// <summary>
/// Click-only 24-hour time selection for the in-calendar event editor.
/// The Popup is opened by an explicit user click, like WPF DatePicker's calendar.
/// This control never opens or activates a separate application window.
/// </summary>
internal sealed class CalendarTimePicker : Grid
{
    private readonly Button _display;
    private readonly ListBox _hours;
    private readonly ListBox _minutes;
    private readonly Popup _popup;
    private readonly TextBox _typed;
    private bool _suppressChange;

    public TimeSpan SelectedTime { get; private set; }
    // Raised only for user input (list selection or typed time), never by SetTime.
    public event EventHandler? TimeChanged;

    public CalendarTimePicker(TimeSpan initialTime)
    {
        if (initialTime < TimeSpan.Zero || initialTime >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(initialTime));

        SelectedTime = new TimeSpan(initialTime.Hours, initialTime.Minutes, 0);
        Width = 137;
        HorizontalAlignment = HorizontalAlignment.Left;

        _display = new Button
        {
            Content = DisplayText(),
            ToolTip = "클릭하여 시와 분 선택",
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8, 4, 8, 4),
            MinHeight = 30
        };
        // Shared dark button template keeps hover/pressed states dark (the stock template
        // turned it light blue behind white text).
        if (CyanDeck.Views.DeckThemeResources.ControlStyle("DarkButtonStyle") is { } displayStyle)
            _display.Style = displayStyle;
        else
        {
            _display.Background = new SolidColorBrush(Color.FromRgb(45, 51, 65));
            _display.Foreground = Brushes.White;
            _display.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 117, 141));
        }
        Children.Add(_display);

        _hours = CreateSelectionList(84, 170);
        _minutes = CreateSelectionList(84, 170);
        for (var hour = 0; hour < 24; hour++)
            _hours.Items.Add(hour.ToString("D2"));
        // 5-minute steps keep the list short; an existing odd minute stays selectable.
        foreach (var minute in CalendarEditorTimePolicy.MinuteChoices(SelectedTime.Minutes))
            _minutes.Items.Add(minute.ToString("D2"));

        // Set before subscribing: opening an editor must preserve existing seconds/minutes.
        _hours.SelectedIndex = SelectedTime.Hours;
        _minutes.SelectedItem = SelectedTime.Minutes.ToString("D2");
        _hours.SelectionChanged += (_, _) => ApplySelection();
        _minutes.SelectionChanged += (_, _) => ApplySelection();

        var lists = new StackPanel { Orientation = Orientation.Horizontal };
        lists.Children.Add(WithHeading("시 (00~23)", _hours));
        lists.Children.Add(WithHeading("분 (5분 단위)", _minutes));

        // Keyboard entry, e.g. "1430" or "14:30"; Enter applies it.
        _typed = new TextBox
        {
            MinWidth = 168, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(4, 2, 4, 2),
            ToolTip = "직접 입력 후 Enter (예: 1430, 14:30)"
        };
        if (CyanDeck.Views.DeckThemeResources.ControlStyle("DarkTextBoxStyle") is { } typedStyle)
            _typed.Style = typedStyle;
        else
        {
            _typed.Background = new SolidColorBrush(Color.FromRgb(42, 49, 62));
            _typed.Foreground = Brushes.White;
        }
        _typed.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; ApplyTyped(closePopup: true); }
            else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; _typed.Text = ""; _popup!.IsOpen = false; }
        };
        _typed.LostKeyboardFocus += (_, _) => { if (_typed.Text.Trim().Length > 0) ApplyTyped(closePopup: false); };

        var confirm = new Button
        {
            Content = "시간 선택 완료", MinHeight = 28,
            Margin = new Thickness(0, 8, 0, 0)
        };
        if (CyanDeck.Views.DeckThemeResources.ControlStyle("DeckPrimaryButtonStyle") is { } confirmStyle)
        {
            confirm.Style = confirmStyle;
            confirm.Padding = new Thickness(10, 3, 10, 3);
        }
        else
        {
            confirm.Background = new SolidColorBrush(Color.FromRgb(62, 85, 113));
            confirm.Foreground = Brushes.White;
        }
        // Bind the handler after _popup is initialized, so it cannot capture
        // a possibly null field during construction (CS8602).

        var popupContent = new StackPanel { Margin = new Thickness(9) };
        popupContent.Children.Add(new TextBlock
        {
            Text = "시간 선택 · 24시간제", Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6)
        });
        popupContent.Children.Add(_typed);
        popupContent.Children.Add(lists);
        popupContent.Children.Add(confirm);

        _popup = new Popup
        {
            PlacementTarget = _display,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(33, 39, 50)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(112, 128, 150)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Child = popupContent
            }
        };
        // Register handlers only after the Popup field has been assigned.
        // This Popup is associated with the existing editor; no Show/Activate/Win32 Z-order calls.
        confirm.Click += (_, _) => _popup.IsOpen = false;
        _display.Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;
        _popup.Opened += (_, _) =>
        {
            _hours.ScrollIntoView(_hours.SelectedItem);
            _minutes.ScrollIntoView(_minutes.SelectedItem);
        };
        Unloaded += (_, _) => _popup.IsOpen = false;
    }

    private static ListBox CreateSelectionList(double width, double height)
    {
        var list = new ListBox
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromRgb(42, 49, 62)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(84, 97, 117))
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }

    private static StackPanel WithHeading(string heading, ListBox list)
    {
        var column = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
        column.Children.Add(new TextBlock
        {
            Text = heading, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3)
        });
        column.Children.Add(list);
        return column;
    }

    private void ApplySelection()
    {
        if (_suppressChange) return;
        if (_hours.SelectedIndex < 0 || _minutes.SelectedItem is not string minuteText
            || !int.TryParse(minuteText, out var minute)) return;
        var selected = new TimeSpan(_hours.SelectedIndex, minute, 0);
        if (selected == SelectedTime) return;
        SelectedTime = selected;
        _display.Content = DisplayText();
        TimeChanged?.Invoke(this, EventArgs.Empty);
    }

    // Programmatic change (e.g. the editor keeping the event length). Does not
    // raise TimeChanged, so linked pickers cannot feed back into each other.
    public void SetTime(TimeSpan time)
    {
        var normalized = new TimeSpan(((time.Hours % 24) + 24) % 24, time.Minutes, 0);
        _suppressChange = true;
        try
        {
            var minuteText = normalized.Minutes.ToString("D2");
            if (!_minutes.Items.Contains(minuteText))
            {
                var insertAt = 0;
                while (insertAt < _minutes.Items.Count
                       && string.CompareOrdinal((string)_minutes.Items[insertAt], minuteText) < 0)
                    insertAt++;
                _minutes.Items.Insert(insertAt, minuteText);
            }
            _hours.SelectedIndex = normalized.Hours;
            _minutes.SelectedItem = minuteText;
        }
        finally { _suppressChange = false; }
        SelectedTime = normalized;
        _display.Content = DisplayText();
    }

    private void ApplyTyped(bool closePopup)
    {
        if (!CalendarEditorTimePolicy.TryParseTime(_typed.Text, out var typed))
        {
            _typed.ToolTip = "시간을 이해하지 못했습니다. 예: 1430, 14:30";
            _typed.BorderBrush = Brushes.IndianRed;
            return;
        }
        _typed.ClearValue(Control.BorderBrushProperty);
        _typed.ToolTip = "직접 입력 후 Enter (예: 1430, 14:30)";
        _typed.Text = "";
        var changed = typed != SelectedTime;
        SetTime(typed);
        if (changed) TimeChanged?.Invoke(this, EventArgs.Empty);
        if (closePopup) _popup.IsOpen = false;
    }

    private string DisplayText() => $"{SelectedTime.Hours:00}:{SelectedTime.Minutes:00}  ◷";
}
