using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CyanDeck.Interop;

namespace CyanDeck.Services;

/// <summary>
/// Gets the *nearest monitor's* usable bounds in WPF DIPs rather than assuming
/// the primary screen or using physical pixels as DIPs. Shared by Fence and Calendar.
/// </summary>
internal static class MonitorWorkAreaResolver
{
    public static bool TryGetWorkingArea(Window window, out Rect work)
    {
        work = Rect.Empty;
        // Calendar attachment is probed BEFORE Window.Show/Loaded. An HWND can
        // already exist at that point, so a Loaded-only lookup would skip every
        // cold-start bounds correction precisely when it is most useful.
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;
            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            var scale = dpi > 0 ? dpi / 96d : Math.Max(.5, VisualTreeHelper.GetDpi(window).DpiScaleX);
            var left = mi.rcWork.Left / scale;
            var top = mi.rcWork.Top / scale;
            var right = mi.rcWork.Right / scale;
            var bottom = mi.rcWork.Bottom / scale;
            work = new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
            return true;
        }
        catch { return false; }
    }

    public static (double Left, double Top) ClampPosition(double left, double top, double width, double height, Rect work)
    {
        if (work.IsEmpty || !double.IsFinite(left) || !double.IsFinite(top))
            return (left, top);
        width = double.IsFinite(width) ? Math.Max(1, width) : 1;
        height = double.IsFinite(height) ? Math.Max(1, height) : 1;
        return (
            Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height)));
    }
}
