using System.Windows;

namespace CyanDeck.Services;

/// <summary>
/// Non-activating geometric correction only; NEVER changes Z-order or calls Show().
/// All inputs are in the same WPF DIP coordinate space as the selected work area.
/// </summary>
internal static class VisibleWindowBoundsPolicy
{
    public static Rect Fit(Rect desired, Rect work, double minimumWidth, double minimumHeight)
    {
        if (work.IsEmpty || !IsUsable(work)) return desired;
        // Never create a window wider or taller than its actual monitor work area.
        // Small/remote displays must win over a larger design-time minimum.
        var availableWidth = Math.Max(1, work.Width);
        var availableHeight = Math.Max(1, work.Height);
        var defaultWidth = Math.Min(availableWidth, Math.Max(1, minimumWidth));
        var defaultHeight = Math.Min(availableHeight, Math.Max(1, minimumHeight));
        var width = double.IsFinite(desired.Width) && desired.Width > 0
            ? Math.Clamp(desired.Width, defaultWidth, availableWidth) : defaultWidth;
        var height = double.IsFinite(desired.Height) && desired.Height > 0
            ? Math.Clamp(desired.Height, defaultHeight, availableHeight) : defaultHeight;
        var left = double.IsFinite(desired.Left) ? desired.Left : work.Left;
        var top = double.IsFinite(desired.Top) ? desired.Top : work.Top;
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new Rect(left, top, width, height);
    }

    private static bool IsUsable(Rect r) => double.IsFinite(r.Left) && double.IsFinite(r.Top)
        && double.IsFinite(r.Width) && double.IsFinite(r.Height)
        && r.Width > 0 && r.Height > 0;
}
