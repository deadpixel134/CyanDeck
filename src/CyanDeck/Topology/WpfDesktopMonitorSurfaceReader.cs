using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CyanDeck.Topology;

/// <summary>
/// Reads the monitor and DPI currently owning a WPF desktop-surface HWND.
/// It does not enumerate monitors, install hooks, subscribe to display events, or mutate window geometry.
/// The host should call it from its existing Loaded / WM_DPICHANGED / WM_DISPLAYCHANGE recovery paths.
/// </summary>
internal static class WpfDesktopMonitorSurfaceReader
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint MonitorInfoPrimary = 0x00000001;

    internal static bool TryRead(Window window, string surfaceId, out DesktopMonitorSurfaceMetric metric)
    {
        metric = default;
        if (window is null || string.IsNullOrWhiteSpace(surfaceId))
        {
            return false;
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };

        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        uint windowDpi = GetDpiForWindow(hwnd);
        if (windowDpi is < 48 or > 960)
        {
            return false;
        }

        if (!TryGetClientBoundsPx(hwnd, out DesktopPixelRect surfaceBounds))
        {
            return false;
        }

        PresentationSource? source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
        {
            return false;
        }

        Matrix transform = source.CompositionTarget.TransformToDevice;
        double wpfDpiX = 96.0 * transform.M11;
        double wpfDpiY = 96.0 * transform.M22;
        if (!double.IsFinite(wpfDpiX) || !double.IsFinite(wpfDpiY) ||
            wpfDpiX is < 48 or > 960 || wpfDpiY is < 48 or > 960)
        {
            return false;
        }

        string monitorKey = string.IsNullOrWhiteSpace(info.DeviceName)
            ? $"HMONITOR:{monitor.ToInt64():X}"
            : info.DeviceName;

        metric = new DesktopMonitorSurfaceMetric(
            surfaceId,
            monitorKey,
            surfaceBounds,
            ToPixelRect(info.Monitor),
            ToPixelRect(info.WorkArea),
            windowDpi,
            wpfDpiX,
            wpfDpiY,
            (info.Flags & MonitorInfoPrimary) != 0);

        return metric.IsValid;
    }

    private static bool TryGetClientBoundsPx(IntPtr hwnd, out DesktopPixelRect bounds)
    {
        bounds = default;
        if (!GetClientRect(hwnd, out NativeRect client) || client.Right <= client.Left || client.Bottom <= client.Top)
        {
            return false;
        }

        var topLeft = new NativePoint { X = client.Left, Y = client.Top };
        var bottomRight = new NativePoint { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight))
        {
            return false;
        }

        bounds = new DesktopPixelRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        return bounds.IsValid;
    }

    private static DesktopPixelRect ToPixelRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }
}
