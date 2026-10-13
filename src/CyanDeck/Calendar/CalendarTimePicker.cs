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

    public TimeSpan SelectedTime { get; private set; }

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
            MinHeight = 30,
            Background = new SolidColorBrush(Color.FromRgb(45, 51, 65)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(100, 117, 141))
        };
        Children.Add(_display);

        _hours = CreateSelectionList(84, 170);
        _minutes = CreateSelectionList(84, 170);
        for (var hour = 0; hour < 24; hour++)
            _hours.Items.Add(hour.ToString("D2"));
        for (var minute = 0; minute < 60; minute++)
            _minutes.Items.Add(minute.ToString("D2"));

        // Set before subscribing: opening an editor must preserve existing seconds/minutes.
        _hours.SelectedIndex = SelectedTime.Hours;
        _minutes.SelectedIndex = SelectedTime.Minutes;
        _hours.SelectionChanged += (_, _) => ApplySelection();
        _minutes.SelectionChanged += (_, _) => ApplySelection();

        var lists = new StackPanel { Orientation = Orientation.Horizontal };
        lists.Children.Add(WithHeading("시 (00~23)", _hours));
        lists.Children.Add(WithHeading("분 (00~59)", _minutes));

        var confirm = new Button
        {
            Content = "시간 선택 완료", MinHeight = 28,
            Margin = new Thickness(0, 8, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(62, 85, 113)),
            Foreground = Brushes.White
        };
        // Bind the handler after _popup is initialized, so it cannot capture
        // a possibly null field during construction (CS8602).

        var popupContent = new StackPanel { Margin = new Thickness(9) };
        popupContent.Children.Add(new TextBlock
        {
            Text = "시간 선택 · 24시간제", Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6)
        });
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
        if (_hours.SelectedIndex < 0 || _minutes.SelectedIndex < 0) return;
        SelectedTime = new TimeSpan(_hours.SelectedIndex, _minutes.SelectedIndex, 0);
        _display.Content = DisplayText();
    }

    private string DisplayText() => $"{SelectedTime.Hours:00}:{SelectedTime.Minutes:00}  ◷";
}
