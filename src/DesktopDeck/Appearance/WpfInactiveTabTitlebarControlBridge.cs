using System;
using System.Windows;
using System.Windows.Input;

namespace DesktopDeck.Appearance;

/// <summary>
/// Event bridge for an individual tab header. The host owns the actual visibility binding/style.
/// This class never marks input handled and never changes selection.
/// </summary>
public sealed class WpfInactiveTabTitlebarControlBridge : IDisposable
{
    private readonly FrameworkElement _header;
    private readonly Func<bool> _isSelectedTab;
    private readonly Func<bool> _isHeaderPresented;
    private readonly Func<bool> _isGroupInteractive;
    private readonly Func<InactiveTabTitlebarControlOptions> _getOptions;
    private readonly Action<bool> _applyInactiveOverride;
    private readonly Action _clearInactiveOverride;

    private readonly MouseEventHandler _mouseEnterHandler;
    private readonly MouseEventHandler _mouseLeaveHandler;
    private readonly DependencyPropertyChangedEventHandler _keyboardFocusWithinChangedHandler;
    private readonly RoutedEventHandler _loadedHandler;
    private readonly RoutedEventHandler _unloadedHandler;

    private bool _disposed;

    public WpfInactiveTabTitlebarControlBridge(
        FrameworkElement header,
        Func<bool> isSelectedTab,
        Func<bool> isHeaderPresented,
        Func<bool> isGroupInteractive,
        Func<InactiveTabTitlebarControlOptions> getOptions,
        Action<bool> applyInactiveOverride,
        Action clearInactiveOverride)
    {
        _header = header ?? throw new ArgumentNullException(nameof(header));
        _isSelectedTab = isSelectedTab ?? throw new ArgumentNullException(nameof(isSelectedTab));
        _isHeaderPresented = isHeaderPresented ?? throw new ArgumentNullException(nameof(isHeaderPresented));
        _isGroupInteractive = isGroupInteractive ?? throw new ArgumentNullException(nameof(isGroupInteractive));
        _getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
        _applyInactiveOverride = applyInactiveOverride ?? throw new ArgumentNullException(nameof(applyInactiveOverride));
        _clearInactiveOverride = clearInactiveOverride ?? throw new ArgumentNullException(nameof(clearInactiveOverride));

        _mouseEnterHandler = OnMouseStateChanged;
        _mouseLeaveHandler = OnMouseStateChanged;
        _keyboardFocusWithinChangedHandler = OnKeyboardFocusWithinChanged;
        _loadedHandler = OnLoaded;
        _unloadedHandler = OnUnloaded;

        _header.MouseEnter += _mouseEnterHandler;
        _header.MouseLeave += _mouseLeaveHandler;
        _header.IsKeyboardFocusWithinChanged += _keyboardFocusWithinChangedHandler;
        _header.Loaded += _loadedHandler;
        _header.Unloaded += _unloadedHandler;

        Refresh();
    }

    /// <summary>
    /// Call when tab selection, settings, header presentation, group interactivity, or managed policy changes.
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var context = new InactiveTabTitlebarControlContext(
            IsTabSelected: SafeRead(_isSelectedTab, fallback: true),
            IsHeaderPresented: SafeRead(_isHeaderPresented, fallback: false),
            IsPointerOverHeader: _header.IsMouseOver,
            IsKeyboardFocusWithinHeader: _header.IsKeyboardFocusWithin,
            IsGroupInteractive: SafeRead(_isGroupInteractive, fallback: false));

        InactiveTabTitlebarControlOptions options;
        try
        {
            options = _getOptions() ?? new InactiveTabTitlebarControlOptions();
        }
        catch
        {
            options = new InactiveTabTitlebarControlOptions();
        }

        var decision = InactiveTabTitlebarControlPolicy.Evaluate(options, context);
        ApplyDecision(decision);
    }

    private void ApplyDecision(InactiveTabTitlebarControlDecision decision)
    {
        if (!decision.AppliesInactiveOverride)
        {
            SafeClearOverride();
            return;
        }

        try
        {
            _applyInactiveOverride(decision.ShowControls);
        }
        catch
        {
            // UI decoration failure must not alter tab selection or commands.
            SafeClearOverride();
        }
    }

    private void OnMouseStateChanged(object sender, MouseEventArgs e) => Refresh();

    private void OnKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void OnUnloaded(object sender, RoutedEventArgs e) => SafeClearOverride();

    private static bool SafeRead(Func<bool> getter, bool fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private void SafeClearOverride()
    {
        try
        {
            _clearInactiveOverride();
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _header.MouseEnter -= _mouseEnterHandler;
        _header.MouseLeave -= _mouseLeaveHandler;
        _header.IsKeyboardFocusWithinChanged -= _keyboardFocusWithinChangedHandler;
        _header.Loaded -= _loadedHandler;
        _header.Unloaded -= _unloadedHandler;

        SafeClearOverride();
    }
}
