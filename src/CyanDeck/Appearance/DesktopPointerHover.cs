using System.Windows;
using System.Windows.Interop;
using CyanDeck.Interop;
using Forms = System.Windows.Forms;

namespace CyanDeck.Appearance;

/// <summary>
/// Cross-check WPF's mouse-over flag against the actual physical cursor position.
/// Desktop-band reparenting and layered HWND startup can leave a transient/stale
/// WPF hover state on multiple independent windows until the pointer next moves.
/// This is presentation-only; it must never synthesize input or move the cursor.
/// </summary>
internal static class DesktopPointerHover
{
    internal static bool IsActuallyOver(FrameworkElement element)
    {
        if (!element.IsLoaded || !element.IsVisible || !element.IsMouseOver)
            return false;

        var window = element as Window ?? Window.GetWindow(element);
        if (window is null || !window.IsVisible) return false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        var cursor = Forms.Cursor.Position; // physical virtual-desktop pixels, including negative monitor coordinates
        return ContainsScreenPoint(cursor.X, cursor.Y, rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    internal static bool ContainsScreenPoint(
        int cursorX, int cursorY, int left, int top, int right, int bottom)
        => right > left && bottom > top
            && cursorX >= left && cursorX < right
            && cursorY >= top && cursorY < bottom;
}
