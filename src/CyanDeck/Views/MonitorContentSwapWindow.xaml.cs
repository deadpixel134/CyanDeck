using System.Windows;
using CyanDeck.Layout;

namespace CyanDeck.Views;

public partial class MonitorContentSwapWindow : Window
{
    private sealed record MonitorChoice(string MonitorId, string Label);

    private readonly IReadOnlyList<MonitorSwapDescriptor> _monitors;

    internal MonitorContentSwapWindow(IReadOnlyList<MonitorSwapDescriptor> monitors)
    {
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        InitializeComponent();

        var choices = _monitors.Select(m => new MonitorChoice(
            m.MonitorId,
            $"{m.DisplayName} · {(int)Math.Round(m.BoundsPx.Width)}×{(int)Math.Round(m.BoundsPx.Height)}" +
            (m.IsPrimary ? " · 주 모니터" : string.Empty))).ToArray();

        FirstMonitorCombo.ItemsSource = choices;
        SecondMonitorCombo.ItemsSource = choices;
        if (choices.Length > 0) FirstMonitorCombo.SelectedIndex = 0;
        if (choices.Length > 1) SecondMonitorCombo.SelectedIndex = 1;
        ValidateSelection();
    }

    internal string FirstMonitorId =>
        (FirstMonitorCombo.SelectedItem as MonitorChoice)?.MonitorId ?? string.Empty;

    internal string SecondMonitorId =>
        (SecondMonitorCombo.SelectedItem as MonitorChoice)?.MonitorId ?? string.Empty;

    internal MonitorContentSwapOptions Options => new()
    {
        IncludeLockedGroups = IncludeLockedGroupsCheck.IsChecked != false,
        ScaleSizeWithWorkingArea = ScaleSizeCheck.IsChecked != false,
    };

    private void MonitorSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ValidateSelection();

    private void ValidateSelection()
    {
        if (SwapButton is null || ValidationText is null) return;
        var valid = !string.IsNullOrWhiteSpace(FirstMonitorId) &&
                    !string.IsNullOrWhiteSpace(SecondMonitorId) &&
                    !string.Equals(FirstMonitorId, SecondMonitorId, StringComparison.OrdinalIgnoreCase);
        SwapButton.IsEnabled = valid;
        ValidationText.Text = valid ? string.Empty : "서로 다른 두 화면을 선택하세요.";
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        ValidateSelection();
        if (!SwapButton.IsEnabled) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
