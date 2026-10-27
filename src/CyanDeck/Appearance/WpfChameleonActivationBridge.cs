using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CyanDeck.Interop;

namespace CyanDeck.Appearance;

/// <summary>
/// Per-group WPF adapter for v7.22. The foreground source is shared process-wide and is not owned here.
/// The host supplies the existing v1.8-style foreground classifier so this overlay does not duplicate
/// Explorer desktop-window discovery or CyanDeck window ownership logic.
/// </summary>
internal sealed class WpfChameleonActivationBridge : IDisposable
{
    private readonly FrameworkElement _groupRoot;
    private readonly WindowsForegroundEventSource _foregroundSource;
    private readonly Func<IntPtr, bool> _classifyDesktopOrCyanDeckForeground;
    private readonly Func<ChameleonActivationContext> _readContext;
    private readonly Func<ChameleonActivationOptions> _readOptions;
    private readonly Action<ChameleonPresentationDecision> _applyDecision;

    private readonly MouseEventHandler _mouseEnterHandler;
    private readonly MouseEventHandler _mouseLeaveHandler;
    private readonly RoutedEventHandler _loadedHandler;
    private readonly RoutedEventHandler _unloadedHandler;
    private DispatcherTimer? _pointerSettleTimer;

    private bool _isDesktopOrCyanDeckForeground;
    private bool _attached;
    private bool _disposed;

    internal WpfChameleonActivationBridge(
        FrameworkElement groupRoot,
        WindowsForegroundEventSource foregroundSource,
        Func<IntPtr, bool> isDesktopOrCyanDeckForeground,
        Func<ChameleonActivationContext> readContext,
        Func<ChameleonActivationOptions> readOptions,
        Action<ChameleonPresentationDecision> applyDecision)
    {
        _groupRoot = groupRoot ?? throw new ArgumentNullException(nameof(groupRoot));
        _foregroundSource = foregroundSource ?? throw new ArgumentNullException(nameof(foregroundSource));
        _classifyDesktopOrCyanDeckForeground = isDesktopOrCyanDeckForeground
            ?? throw new ArgumentNullException(nameof(isDesktopOrCyanDeckForeground));
        _readContext = readContext ?? throw new ArgumentNullException(nameof(readContext));
        _readOptions = readOptions ?? throw new ArgumentNullException(nameof(readOptions));
        _applyDecision = applyDecision ?? throw new ArgumentNullException(nameof(applyDecision));

        _mouseEnterHandler = OnPointerStateChanged;
        _mouseLeaveHandler = OnPointerStateChanged;
        _loadedHandler = OnLoaded;
        _unloadedHandler = OnUnloaded;

        _groupRoot.Loaded += _loadedHandler;
        _groupRoot.Unloaded += _unloadedHandler;

        if (_groupRoot.IsLoaded)
        {
            AttachRuntimeHandlers();
            RefreshForegroundFromCurrentWindow();
            Refresh();
        }
    }

    internal void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        ChameleonActivationContext host = SafeReadContext();
        ChameleonActivationContext context = host with
        {
            IsPointerOverGroup = DesktopPointerHover.IsActuallyOver(_groupRoot),
            IsDesktopOrCyanDeckForeground = _isDesktopOrCyanDeckForeground,
        };

        _applyDecision(ChameleonActivationPolicy.Decide(context, SafeReadOptions()));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachRuntimeHandlers();
        RefreshForegroundFromCurrentWindow();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachRuntimeHandlers();
    }

    private void OnPointerStateChanged(object sender, MouseEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // WPF can synthesize MouseEnter/MouseLeave when a layered top-level window is re-ordered
        // or another popup HWND is created/destroyed under a stationary cursor. Those structural
        // transitions must not drive Chameleon opacity while the user is idle; otherwise a fence
        // appears to activate/deactivate every few seconds even though no input occurred.
        if (!WasRecentRealUserInput(TimeSpan.FromMilliseconds(900)))
        {
            return;
        }

        _pointerSettleTimer ??= new DispatcherTimer(DispatcherPriority.Input, _groupRoot.Dispatcher);
        _pointerSettleTimer.Stop();
        _pointerSettleTimer.Interval = TimeSpan.FromMilliseconds(140);
        _pointerSettleTimer.Tick -= PointerSettleTimer_Tick;
        _pointerSettleTimer.Tick += PointerSettleTimer_Tick;
        _pointerSettleTimer.Start();
    }

    private void PointerSettleTimer_Tick(object? sender, EventArgs e)
    {
        _pointerSettleTimer?.Stop();
        if (_disposed)
        {
            return;
        }

        // The delayed check filters brief enter/leave pairs produced by popup/z-order churn.
        // Real pointer movement updated LASTINPUTINFO, so an intentional hover/leave still applies.
        if (WasRecentRealUserInput(TimeSpan.FromMilliseconds(1400)))
        {
            Refresh();
        }
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (_disposed)
        {
            return;
        }

        // Foreground changes are intentionally presentation-neutral. A focus switch can generate
        // several transient foreground HWNDs (popup, owner, shell surface) in a very short period.
        // Driving Shell.Opacity directly from that process-wide stream makes transparent WPF
        // windows visibly pulse even when their native Z-order never changes. Keep the classifier
        // state current, but defer any presentation update until a real local interaction (hover,
        // explicit refresh, settings change, Peek transition) asks for one.
        _isDesktopOrCyanDeckForeground = SafeClassifyForeground(hwnd);
    }

    private void RefreshForegroundFromCurrentWindow()
    {
        _isDesktopOrCyanDeckForeground = SafeClassifyForeground(
            _foregroundSource.CurrentForegroundWindow);
    }

    private bool SafeClassifyForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return _classifyDesktopOrCyanDeckForeground(hwnd);
        }
        catch
        {
            // Fail toward blended presentation when foreground ownership is uncertain.
            return false;
        }
    }

    private static bool WasRecentRealUserInput(TimeSpan maximumAge)
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            // If Windows cannot provide the input timestamp, preserve functionality rather than
            // permanently suppressing hover presentation.
            return true;
        }

        var now = unchecked((uint)Environment.TickCount);
        var elapsed = unchecked(now - info.dwTime);
        return elapsed <= Math.Max(0d, maximumAge.TotalMilliseconds);
    }

    private ChameleonActivationContext SafeReadContext()
    {
        try
        {
            return _readContext();
        }
        catch
        {
            return new ChameleonActivationContext(
                ChameleonEnabled: false,
                GroupUsesChameleon: false,
                IsPointerOverGroup: false,
                IsDesktopOrCyanDeckForeground: false,
                IsGroupPresented: false,
                DeferToHostPresentation: true);
        }
    }

    private ChameleonActivationOptions SafeReadOptions()
    {
        try
        {
            return _readOptions();
        }
        catch
        {
            return ChameleonActivationOptions.Default;
        }
    }

    private void AttachRuntimeHandlers()
    {
        if (_attached || _disposed)
        {
            return;
        }

        _groupRoot.MouseEnter += _mouseEnterHandler;
        _groupRoot.MouseLeave += _mouseLeaveHandler;
        _foregroundSource.ForegroundChanged += OnForegroundChanged;
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
        _foregroundSource.ForegroundChanged -= OnForegroundChanged;
        _attached = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pointerSettleTimer?.Stop();
        _pointerSettleTimer = null;
        DetachRuntimeHandlers();
        _groupRoot.Loaded -= _loadedHandler;
        _groupRoot.Unloaded -= _unloadedHandler;
    }
}
