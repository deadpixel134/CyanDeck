using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CyanDeck.Interop;
using CyanDeck.Services;
using CyanDeck.Topology;
using Forms = System.Windows.Forms;

namespace CyanDeck.Input;

/// <summary>
/// Hosts one transparent, mouse-click-through WPF touch surface per monitor on the Explorer desktop.
/// The surfaces exist only while CyanDeck owns/hides the native desktop icon ListView, avoiding
/// interference with native icon touch interaction when Windows desktop icons are exposed.
/// </summary>
internal sealed class DesktopPageTouchSurfaceHost : IDisposable
{
    private readonly DesktopHost _desktopHost;
    private readonly Func<DesktopPageTouchSwipeOptions> _getOptions;
    private readonly Func<DesktopPageTouchSwipeDirection, bool> _canNavigate;
    private readonly Action<string, DesktopPageTouchSwipeDirection, double> _preview;
    private readonly Action<string> _cancelPreview;
    private readonly Action<string, DesktopPageTouchSwipeDirection> _commit;
    private readonly List<SurfaceRegistration> _surfaces = [];
    private bool _enabled;
    private bool _disposed;

    internal DesktopPageTouchSurfaceHost(
        DesktopHost desktopHost,
        Func<DesktopPageTouchSwipeOptions> getOptions,
        Func<DesktopPageTouchSwipeDirection, bool> canNavigate,
        Action<string, DesktopPageTouchSwipeDirection, double> preview,
        Action<string> cancelPreview,
        Action<string, DesktopPageTouchSwipeDirection> commit)
    {
        _desktopHost = desktopHost ?? throw new ArgumentNullException(nameof(desktopHost));
        _getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
        _canNavigate = canNavigate ?? throw new ArgumentNullException(nameof(canNavigate));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _cancelPreview = cancelPreview ?? throw new ArgumentNullException(nameof(cancelPreview));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
    }

    internal void Refresh(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _enabled = enabled;
        CloseSurfaces();
        if (!enabled)
        {
            return;
        }

        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            Rectangle bounds = screen.Bounds;
            string surfaceId = $"touch:{screen.DeviceName}";
            var window = new DesktopTouchSurfaceWindow();
            var bridge = new WpfDesktopPageTouchSwipeBridge(
                window,
                _getOptions,
                isBlockedSource: _ => false,
                _canNavigate,
                (direction, progress) => _preview(surfaceId, direction, progress),
                () => _cancelPreview(surfaceId),
                direction => _commit(surfaceId, direction));

            try
            {
                window.Show();
                _desktopHost.AttachTouchSurface(window, bounds);
                _surfaces.Add(new SurfaceRegistration(surfaceId, window, bridge, bounds));
            }
            catch (Exception ex)
            {
                bridge.Dispose();
                try { window.Close(); } catch { }
                DiagnosticsLog.Error("Failed to attach Desktop Pages touch surface", ex);
            }
        }

        DiagnosticsLog.Info($"Desktop Pages touch surfaces: enabled={enabled}; monitors={_surfaces.Count}");
    }


    internal IReadOnlyList<DesktopMonitorSurfaceMetric> ReadTopologyMetrics()
    {
        if (_disposed || !_enabled || _surfaces.Count == 0)
        {
            return Array.Empty<DesktopMonitorSurfaceMetric>();
        }

        var metrics = new List<DesktopMonitorSurfaceMetric>(_surfaces.Count);
        foreach (SurfaceRegistration surface in _surfaces)
        {
            if (WpfDesktopMonitorSurfaceReader.TryRead(surface.Window, surface.SurfaceId, out DesktopMonitorSurfaceMetric metric))
            {
                metrics.Add(metric);
            }
        }
        return metrics;
    }

    internal void EnsureAttached()
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        foreach (SurfaceRegistration surface in _surfaces.ToArray())
        {
            try
            {
                _desktopHost.EnsureTouchSurfaceAttached(surface.Window, surface.Bounds);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Failed to reattach Desktop Pages touch surface", ex);
            }
        }
    }

    private void CloseSurfaces()
    {
        foreach (SurfaceRegistration surface in _surfaces)
        {
            try { surface.Bridge.Dispose(); } catch { }
            try { surface.Window.Close(); } catch { }
        }
        _surfaces.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enabled = false;
        CloseSurfaces();
    }

    private sealed record SurfaceRegistration(
        string SurfaceId,
        DesktopTouchSurfaceWindow Window,
        WpfDesktopPageTouchSwipeBridge Bridge,
        Rectangle Bounds);
}

/// <summary>
/// Invisible desktop child used only as a direct-touch target. Returning HTTRANSPARENT keeps mouse
/// hit testing on Explorer while Windows continues to deliver WM_TOUCH to this HWND.
/// </summary>
internal sealed class DesktopTouchSurfaceWindow : Window
{
    private HwndSource? _source;

    internal DesktopTouchSurfaceWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowsTransparency = true;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(NativeMethods.HTTRANSPARENT);
        }

        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_source is not null)
        {
            _source.RemoveHook(WindowMessageHook);
            _source = null;
        }
    }
}
