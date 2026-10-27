using System.Drawing;

namespace CyanDeck.Layout;

/// <summary>
/// Automatic slot for a NEW loose desktop icon (one without a saved position).
/// The work area is in physical pixels (the app is Per-Monitor-V2 aware), so every
/// logical (DIP) cell size, item size and edge inset is multiplied by that monitor's
/// scale. Instead of a running ordinal, the first column-major cell whose icon rectangle
/// does not overlap an existing icon or a group window is chosen. Positions the user
/// already set are never moved; this planner is only asked for unplaced items.
/// Pure logic (System.Drawing primitives only) so it can be verified without WPF.
/// </summary>
internal static class LooseDesktopAutoPlacementPlanner
{
    internal static double NormalizeScale(double scale)
        => double.IsFinite(scale) && scale >= 0.5 && scale <= 10d ? scale : 1d;

    internal static int ScaleDip(double dip, double scale)
        => (int)Math.Ceiling(dip * NormalizeScale(scale));

    internal static Point FindSlot(Rectangle workArea, double scale, IReadOnlyList<Rectangle> occupied, int fallbackOrdinal)
    {
        var s = NormalizeScale(scale);
        var slotWidth = Math.Max(1, ScaleDip(LooseDesktopIconLayoutProfile.LooseAutoCellWidthDip, s));
        var slotHeight = Math.Max(1, ScaleDip(LooseDesktopIconLayoutProfile.LooseAutoCellHeightDip, s));
        var inset = ScaleDip(LooseDesktopIconLayoutProfile.EdgeInsetDip, s);
        var itemWidth = Math.Max(1, ScaleDip(LooseDesktopIconLayoutProfile.ItemWidthDip, s));
        var itemHeight = Math.Max(1, ScaleDip(LooseDesktopIconLayoutProfile.ItemHeightDip, s));
        var columns = Math.Max(1, (workArea.Width - inset * 2) / slotWidth);
        var rows = Math.Max(1, (workArea.Height - inset * 2) / slotHeight);
        var blockers = occupied.Where(r => r.Width > 0 && r.Height > 0).ToArray();

        for (var column = 0; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                var x = workArea.Left + inset + column * slotWidth;
                var y = workArea.Top + inset + row * slotHeight;
                var candidate = new Rectangle(x, y, itemWidth, itemHeight);
                if (!blockers.Any(b => b.IntersectsWith(candidate))) return new Point(x, y);
            }
        }

        // Every cell is taken: keep the previous ordinal layout (now DPI-scaled) as a
        // deterministic last resort instead of stacking every new icon on one cell.
        var ordinal = Math.Max(0, fallbackOrdinal) % (columns * rows);
        return new Point(
            workArea.Left + inset + ordinal / rows * slotWidth,
            workArea.Top + inset + ordinal % rows * slotHeight);
    }
}
