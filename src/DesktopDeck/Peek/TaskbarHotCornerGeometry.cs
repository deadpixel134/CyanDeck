using System;

namespace DesktopDeck.Peek;

public static class TaskbarHotCornerGeometry
{
    public static PixelRect Compute(PrimaryTaskbarMetric taskbar, PeekHotCornerOptions? options = null)
    {
        if (!taskbar.IsValid)
        {
            return default;
        }

        var thickness = (options ?? new PeekHotCornerOptions()).GetNormalizedHitThicknessPx();
        var bounds = taskbar.BoundsPx;

        return taskbar.Edge switch
        {
            TaskbarEdge.Top or TaskbarEdge.Bottom => new PixelRect(
                Left: Math.Max(bounds.Left, bounds.Right - Math.Min(thickness, bounds.Width)),
                Top: bounds.Top,
                Right: bounds.Right,
                Bottom: bounds.Bottom),

            TaskbarEdge.Left or TaskbarEdge.Right => new PixelRect(
                Left: bounds.Left,
                Top: Math.Max(bounds.Top, bounds.Bottom - Math.Min(thickness, bounds.Height)),
                Right: bounds.Right,
                Bottom: bounds.Bottom),

            _ => default,
        };
    }
}
