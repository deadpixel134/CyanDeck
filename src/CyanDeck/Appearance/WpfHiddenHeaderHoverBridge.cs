using System.Windows;
using System.Windows.Input;

namespace CyanDeck.Appearance;

/// <summary>
/// Event-driven WPF bridge for the Hidden/Never header exception used by Folder Portals and tab groups.
/// It observes pointer/focus state only. It never captures input, starts a timer, selects a tab, or invokes commands.
/// </summary>
internal sealed class WpfHiddenHeaderHoverBridge : IDisposable
{
    private readonly FrameworkElement _groupRoot;
    private readonly FrameworkElement _headerRoot;
    private readonly Func<HiddenHeaderHoverContext> _readContext;
    private readonly Func<HiddenHeaderHoverOptions> _readOptions;
    private readonly Action<HiddenHeaderHoverDecision> _applyDecision;

    private readonly MouseEventHandler _mouseEnterHandler;
    private readonly MouseEventHandler _mouseLeaveHandler;
    private readonly DependencyPropertyChangedEventHandler _focusChangedHandler;
    private readonly RoutedEventHandler _loadedHandler;
    private readonly RoutedEventHandler _unloadedHandler;

    private bool _pointerOverGroup;
    private bool _attached;
    private bool _disposed;

    internal WpfHiddenHeaderHoverBridge(
        FrameworkElement groupRoot,
        FrameworkElement headerRoot,
        Func<HiddenHeaderHoverContext> readContext,
        Func<HiddenHeaderHoverOptions> readOptions,
        Action<HiddenHeaderHoverDecision> applyDecision)
    {
        _groupRoot = groupRoot ?? throw new ArgumentNullException(nameof(groupRoot));
        _headerRoot = headerRoot ?? throw new ArgumentNullException(nameof(headerRoot));
        _readContext = readContext ?? throw new ArgumentNullException(nameof(readContext));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _applyDecision = applyDecision ?? throw new ArgumentNullException(nameof(applyDecision));

        _mouseEnterHandler = OnMouseEnter;
        _mouseLeaveHandler = OnMouseLeave;
        _focusChangedHandler = OnKeyboardFocusWithinChanged;
        _loadedHandler = OnLoaded;
        _unloadedHandler = OnUnloaded;

        _groupRoot.Loaded += _loadedHandler;
        _groupRoot.Unloaded += _unloadedHandler;
        if (_groupRoot.IsLoaded)
        {
            AttachRuntimeHandlers();
            Refresh();
        }
    }

    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        HiddenHeaderHoverContext host = SafeReadContext();
        HiddenHeaderHoverContext context = host with
        {
            IsPointerOverGroup = _pointerOverGroup || _groupRoot.IsMouseOver,
            IsKeyboardFocusWithinHeader = _headerRoot.IsKeyboardFocusWithin,
        };

        HiddenHeaderHoverDecision decision = HiddenHeaderHoverPolicy.Decide(
            context,
            SafeReadOptions());

        _applyDecision(decision);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachRuntimeHandlers();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pointerOverGroup = false;
        DetachRuntimeHandlers();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _pointerOverGroup = true;
        Refresh();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _pointerOverGroup = false;
        Refresh();
    }

    private void OnKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();

    private void AttachRuntimeHandlers()
    {
        if (_attached || _disposed)
        {
            return;
        }

        _groupRoot.MouseEnter += _mouseEnterHandler;
        _groupRoot.MouseLeave += _mouseLeaveHandler;
        _headerRoot.IsKeyboardFocusWithinChanged += _focusChangedHandler;
        _attached = true;
    }

    private void DetachRuntimeHandlers()
    {
        if (!_attached)
        {
            return;
        }

        _groupRoot.MouseEnter -= _mouseEnterHandler;
        _groupRoot.MouseLeave -= _mouseLeaveHandler;
        _headerRoot.IsKeyboardFocusWithinChanged -= _focusChangedHandler;
        _attached = false;
    }

    private HiddenHeaderHoverContext SafeReadContext()
    {
        try
        {
            return _readContext();
        }
        catch
        {
            // Fail closed: do not unexpectedly reveal a Hidden header if host state cannot be read.
            return new HiddenHeaderHoverContext(
                CyanDeckTitleBarVisibility.Hidden,
                IsFolderPortalGroup: false,
                TabCount: 0,
                IsPointerOverGroup: false,
                IsKeyboardFocusWithinHeader: false,
                IsGroupInteractive: false,
                IsRolledUp: false,
                IsHeaderPresented: false);
        }
    }

    private HiddenHeaderHoverOptions SafeReadOptions()
    {
        try
        {
            return _readOptions();
        }
        catch
        {
            return HiddenHeaderHoverOptions.Default;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachRuntimeHandlers();
        _groupRoot.Loaded -= _loadedHandler;
        _groupRoot.Unloaded -= _unloadedHandler;
        _pointerOverGroup = false;
    }
}
