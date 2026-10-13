using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CyanDeck.Calendar;

// Calendar-scoped color editor: the swatch opens a nonmodal RGB/preset picker.
// Valid #RRGGBB edits persist immediately; slider motion previews immediately
// and coalesces disk checkpoints to keep the WPF input thread responsive.
internal sealed class CalendarColorPicker : StackPanel
{
    private static readonly string[] PresetColors =
    [
        "#F83A22", "#FF929A", "#FFB347", "#FFD166", "#78D390", "#26B7A9",
        "#81C1FF", "#5D91DD", "#A48FFF", "#E58CCB", "#FFFFFF", "#708090"
    ];
    private readonly TextBox _hex;
    private readonly Button _swatch;
    private readonly Popup _popup;
    private readonly Slider _red;
    private readonly Slider _green;
    private readonly Slider _blue;
    private readonly DispatcherTimer _sliderCommit;
    private readonly Action<string> _saveColor;
    private readonly Action<string> _showError;
    private readonly bool _canEdit;
    private bool _updatingFromSlider;
    private bool _syncingSliders;
    private bool _hasPendingSlider;
    private string _lastSaved;

    public CalendarColorPicker(string initialColor, bool canEdit, Action<string> saveColor, Action<string> showError,
        TextBox? externalHex = null, Button? externalSwatch = null)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _canEdit = canEdit;
        _saveColor = saveColor;
        _showError = showError;
        _lastSaved = IsValidHex(initialColor) ? initialColor.ToUpperInvariant() : "#5D91DD";
        _hex = externalHex ?? new TextBox
        {
            Width = 86, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "#RRGGBB · 올바른 HEX 값 입력 시 즉시 저장"
        };
        _hex.Text = _lastSaved;
        _hex.IsEnabled = canEdit;
        if (externalHex is null) Children.Add(_hex);
        _swatch = externalSwatch ?? new Button
        {
            Width = 25, Height = 23, MinHeight = 0, Padding = new Thickness(0),
            Margin = new Thickness(7, 0, 3, 0),
            Cursor = Cursors.Hand, BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            ToolTip = "현재 색상 · 클릭하면 컬러피커 열기"
        };
        _swatch.IsEnabled = canEdit;
        _swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_lastSaved));
        if (externalSwatch is null) Children.Add(_swatch);

        var content = new StackPanel { Margin = new Thickness(10) };
        content.Children.Add(new TextBlock
        {
            Text = "색상 선택", FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8)
        });
        var palette = new WrapPanel { Width = 224 };
        foreach (var preset in PresetColors)
        {
            var sample = preset;
            var quick = new Button
            {
                Width = 29, Height = 25, MinHeight = 0, Padding = new Thickness(0),
                Margin = new Thickness(2),
                BorderBrush = Brushes.Gray,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sample)),
                ToolTip = sample, Cursor = Cursors.Hand
            };
            quick.Click += (_, _) => SetColor(sample);
            palette.Children.Add(quick);
        }
        content.Children.Add(palette);
        content.Children.Add(new TextBlock
        {
            Text = "RGB 미세 조정", Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 10, 0, 3)
        });
        var start = (Color)ColorConverter.ConvertFromString(_lastSaved);
        _red = AddChannel(content, "R", start.R);
        _green = AddChannel(content, "G", start.G);
        _blue = AddChannel(content, "B", start.B);
        content.Children.Add(new TextBlock
        {
            Text = "HEX 입력 또는 색상 선택 시 자동 적용", Foreground = Brushes.LightGray,
            FontSize = 11, Margin = new Thickness(0, 7, 0, 0)
        });
        _popup = new Popup
        {
            PlacementTarget = _swatch, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 41, 54)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(105, 118, 140)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Child = content
            }
        };
        _sliderCommit = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _sliderCommit.Tick += (_, _) => FlushSliderColor();
        _hex.TextChanged += (_, _) => OnHexChanged();
        _red.ValueChanged += (_, _) => OnSliderChanged();
        _green.ValueChanged += (_, _) => OnSliderChanged();
        _blue.ValueChanged += (_, _) => OnSliderChanged();
        _swatch.Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;
        _popup.Closed += (_, _) => FlushSliderColor();
        Unloaded += (_, _) => { FlushSliderColor(); _popup.IsOpen = false; };
    }

    public void SetColor(string color)
    {
        if (!IsValidHex(color)) return;
        _sliderCommit.Stop();
        _hasPendingSlider = false;
        // TextChanged is the one authoritative save path, also for palette picks.
        if (_hex.Text.Equals(color, StringComparison.OrdinalIgnoreCase))
            ApplyColor(color.ToUpperInvariant());
        else
            _hex.Text = color.ToUpperInvariant();
    }

    // Refresh without creating a color override (e.g. restore provider color).
    public void SetDisplayedColor(string color)
    {
        if (!IsValidHex(color)) return;
        _sliderCommit.Stop();
        _hasPendingSlider = false;
        _lastSaved = color.ToUpperInvariant();
        _hex.Text = _lastSaved;
        UpdatePreviewAndSliders(_lastSaved);
    }

    private static Slider AddChannel(Panel parent, string label, byte initial)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var name = new TextBlock { Text = label, Foreground = Brushes.White,
            Width = 18, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        var value = new TextBlock { Foreground = Brushes.White, Width = 35,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        var slider = new Slider { Minimum = 0, Maximum = 255, Value = initial,
            IsSnapToTickEnabled = true, TickFrequency = 1, Width = 157,
            VerticalAlignment = VerticalAlignment.Center };
        value.Text = initial.ToString();
        slider.ValueChanged += (_, _) => value.Text = ((int)slider.Value).ToString();
        row.Children.Add(slider);
        parent.Children.Add(row);
        return slider;
    }

    private void OnHexChanged()
    {
        if (!_canEdit || !IsValidHex(_hex.Text)) return;
        var color = _hex.Text.ToUpperInvariant();
        UpdatePreviewAndSliders(color);
        if (_updatingFromSlider)
        {
            _hasPendingSlider = true;
            _sliderCommit.Stop();
            _sliderCommit.Start();
        }
        else
        {
            _sliderCommit.Stop();
            _hasPendingSlider = false;
            ApplyColor(color);
        }
    }

    private void UpdatePreviewAndSliders(string color)
    {
        var value = (Color)ColorConverter.ConvertFromString(color);
        _swatch.Background = new SolidColorBrush(value);
        if (_syncingSliders) return;
        _syncingSliders = true;
        try { _red.Value = value.R; _green.Value = value.G; _blue.Value = value.B; }
        finally { _syncingSliders = false; }
    }

    private void OnSliderChanged()
    {
        if (_syncingSliders || !_canEdit) return;
        var hex = $"#{(int)_red.Value:X2}{(int)_green.Value:X2}{(int)_blue.Value:X2}";
        _updatingFromSlider = true;
        try { _hex.Text = hex; }
        finally { _updatingFromSlider = false; }
    }

    private void FlushSliderColor()
    {
        _sliderCommit.Stop();
        if (!_hasPendingSlider) return;
        _hasPendingSlider = false;
        if (IsValidHex(_hex.Text)) ApplyColor(_hex.Text.ToUpperInvariant());
    }

    private void ApplyColor(string color)
    {
        if (!_canEdit || color == _lastSaved) return;
        try
        {
            _saveColor(color);
            _lastSaved = color;
        }
        catch (Exception ex)
        {
            _showError("캘린더 색상 저장 실패: " + ex.Message);
        }
    }

    private static bool IsValidHex(string? value)
        => value is not null && Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");
}
