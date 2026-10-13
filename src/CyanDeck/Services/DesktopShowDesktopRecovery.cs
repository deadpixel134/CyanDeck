using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CyanDeck.Interop;

namespace CyanDeck.Services;

/// <summary>
/// Rate-limited, non-activating recovery for desktop-only WPF surfaces minimized/hidden
/// by Windows Show Desktop (Win+D / taskbar corner). Native watchdog ticks are
/// read-only when healthy; no Explorer messages or focus-activation retries.
/// A caller-supplied policy excludes intentional page/quick-hide and failed calendar
/// attachment. All callbacks run on the owning UI Dispatcher.
/// </summary>
internal sealed class DesktopShowDesktopRecovery : IDisposable
{
    private readonly Window _window;
    private readonly Func<bool> _shouldRemainVisible;
    private readonly Action _restoreDesktopBand;
    private readonly Func<bool> _isInDesktopBand;
    private readonly DispatcherTimer _nativeProbe;
    private int _unhealthyProbeCount;
    private readonly DispatcherTimer _timer;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _disposed;
    private bool _restoring;

    internal DesktopShowDesktopRecovery(Window window, Func<bool> shouldRemainVisible,
        Action restoreDesktopBand, Func<bool> isInDesktopBand)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _shouldRemainVisible = shouldRemainVisible ?? throw new ArgumentNullException(nameof(shouldRemainVisible));
        _restoreDesktopBand = restoreDesktopBand ?? throw new ArgumentNullException(nameof(restoreDesktopBand));
        _isInDesktopBand = isInDesktopBand ?? throw new ArgumentNullException(nameof(isInDesktopBand));
        // Win+D can minimize a native HWND without raising WPF StateChanged.
        // Poll only read-only HWND state; never modify windows on healthy ticks.
        _nativeProbe = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _nativeProbe.Tick += OnNativeProbe;
        _nativeProbe.Start();
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _timer.Tick += OnTick;
        _window.StateChanged += OnStateChanged;
        _window.IsVisibleChanged += OnVisibleChanged;
        _window.Closed += OnClosed;
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (_window.WindowState == WindowState.Minimized) Schedule();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is false) Schedule();
    }

    private void Schedule()
    {
        if (_disposed || _restoring || !_shouldRemainVisible()) return;
        if (DateTime.UtcNow - _lastAttemptUtc < TimeSpan.FromSeconds(3)) return;
        _timer.Stop();
        _timer.Start();
    }

    private void OnNativeProbe(object? sender, EventArgs args)
    {
        if (_disposed || _restoring || !_shouldRemainVisible()) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

        // Ordinary application focus changes must never initiate a desktop Z-order move.
        // Native hidden/minimized state, however, is an observable Show Desktop failure.
        var shellForeground = IsExplorerDesktopOrTaskbarForeground();
        var nativeVisible = NativeMethods.IsWindowVisible(hwnd);
        var nativeMinimized = NativeMethods.IsIconic(hwnd);
        var wpfMinimized = _window.WindowState == WindowState.Minimized;
        var displaced = shellForeground && !_isInDesktopBand();
        var needsRecovery = DesktopShowDesktopRecoveryPolicy.NeedsRecovery(
            true, _window.IsVisible, wpfMinimized, nativeVisible,
            nativeMinimized, shellForeground, !displaced);
        if (!needsRecovery)
        {
            _unhealthyProbeCount = 0;
            return;
        }

        _unhealthyProbeCount = Math.Min(10, _unhealthyProbeCount + 1);
        var hidden = !nativeVisible || nativeMinimized || !_window.IsVisible || wpfMinimized;
        if (!DesktopShowDesktopRecoveryPolicy.ShouldSchedulePersistentRepair(
            needsRecovery, hidden, _unhealthyProbeCount, DateTime.UtcNow,
            _lastAttemptUtc, _timer.IsEnabled)) return;
        DiagnosticsLog.Info($"Show Desktop read-only probe detected missing surface: minimized={nativeMinimized}, "
            + $"nativeVisible={nativeVisible}, wpfVisible={_window.IsVisible}, "
            + $"desktopForeground={shellForeground}, displaced={displaced}; "
            + $"unhealthyProbes={_unhealthyProbeCount}; recovery=rate-limited");
        Schedule();
    }

    private static bool IsExplorerDesktopOrTaskbarForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(progman, out var explorerPid);
        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (explorerPid == 0 || foregroundPid != explorerPid) return false;
        var name = new StringBuilder(64);
        NativeMethods.GetClassName(foreground, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView"
            or "SysListView32" or "Shell_TrayWnd";
    }

    private void OnTick(object? sender, EventArgs args)
    {
        _timer.Stop();
        if (_disposed || _restoring || !_shouldRemainVisible()) return;
        var currentHwnd = new WindowInteropHelper(_window).Handle;
        if (currentHwnd == IntPtr.Zero) return;
        var nativeHidden = NativeMethods.IsIconic(currentHwnd) || !NativeMethods.IsWindowVisible(currentHwnd);
        var wpfHidden = _window.WindowState == WindowState.Minimized || !_window.IsVisible;
        var shellForeground = IsExplorerDesktopOrTaskbarForeground();
        var displaced = shellForeground && !_isInDesktopBand();
        if (!DesktopShowDesktopRecoveryPolicy.NeedsRecovery(true, _window.IsVisible,
            _window.WindowState == WindowState.Minimized,
            NativeMethods.IsWindowVisible(currentHwnd), NativeMethods.IsIconic(currentHwnd),
            shellForeground, !displaced)) return;
        _restoring = true;
        _lastAttemptUtc = DateTime.UtcNow;
        try
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero) return;
            // SW_SHOWNOACTIVATE restores a minimized HWND without stealing focus.
            // Do NOT use Window.Activate(), SW_RESTORE or HWND_TOPMOST here.
            if (nativeHidden || wpfHidden)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
                if (!_window.IsVisible) _window.Show(); // ShowActivated=false, never Activate().
            }
            // Realign only after a native disappearance, not on normal focus changes.
            // DesktopHost no-ops if the HWND is already in the desktop band.
            _restoreDesktopBand();
            // A successful call does not guarantee Explorer kept the window in
            // its band. Leave the incident retryable if Windows reordered hosts.
            if (_window.IsVisible && _window.WindowState != WindowState.Minimized &&
                NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd) &&
                _isInDesktopBand())
            {
                _unhealthyProbeCount = 0;
                DiagnosticsLog.Info("Show Desktop automatic desktop-band recovery verified");
            }
            else
                DiagnosticsLog.Warn("Show Desktop recovery attempted; surface still outside desktop band or hidden");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("Show Desktop surface restoration was skipped: " + ex.Message);
        }
        finally { _restoring = false; }
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _nativeProbe.Stop();
        _nativeProbe.Tick -= OnNativeProbe;
        _timer.Tick -= OnTick;
        _window.StateChanged -= OnStateChanged;
        _window.IsVisibleChanged -= OnVisibleChanged;
        _window.Closed -= OnClosed;
    }
}
