using System;
using System.Collections.Generic;
using System.Linq;
using CyanDeck.Topology;

namespace CyanDeck.Services;

/// <summary>
/// Decides whether a dropped group moves to another desktop page. The decision uses the
/// pointer that released the group, with the same outer-edge / monitor-seam rules as the
/// pointer page gesture, so magnetic edge snapping or a group straddling two monitors never
/// counts as "dragged past the edge". Pure and WPF-free so the two-monitor cases are tested.
/// </summary>
internal static class GroupPageDropEdgePolicy
{
    // Same hit widths as the pointer edge gesture (AppController.ResolveDesktopPageEdgeDirection).
    internal const int LeftEdgeHitPx = 3;
    internal const int RightEdgeHitPx = 4;
    // The pointer must rest at the edge this long before a drop switches pages.
    internal const int DwellMilliseconds = 300;

    internal static int ResolveDirection(
        DesktopMonitorTopology? topology,
        string surfaceId,
        int cursorX,
        int cursorY,
        DesktopMonitorTopologyOptions? options)
    {
        if (topology is null || !topology.TryGetSurface(surfaceId, out var surface)) return 0;
        var bounds = surface.MonitorBoundsPx;
        if (!bounds.ContainsY(cursorY)) return 0;

        DesktopMonitorHorizontalEdge edge;
        int direction;
        if (cursorX <= bounds.Left + LeftEdgeHitPx)
        {
            edge = DesktopMonitorHorizontalEdge.Left;
            direction = -1;
        }
        else if (cursorX >= bounds.Right - RightEdgeHitPx)
        {
            edge = DesktopMonitorHorizontalEdge.Right;
            direction = 1;
        }
        else
        {
            return 0;
        }

        var classification = topology.ClassifyHorizontalEdgeAtScreenY(surfaceId, edge, cursorY, options);
        return classification.Decision switch
        {
            DesktopPageEdgeNavigationDecision.AllowedOuterEdge => direction,
            DesktopPageEdgeNavigationDecision.AllowedDetachedEdge => direction,
            DesktopPageEdgeNavigationDecision.AllowedInteriorSeam => direction,
            _ => 0,
        };
    }

    internal static bool IsDwellSatisfied(int hintDirection, long hintSinceMs, int dropDirection, long nowMs, int dwellMs = DwellMilliseconds)
        => dropDirection != 0 && hintDirection == dropDirection && hintSinceMs > 0 && nowMs - hintSinceMs >= dwellMs;

    /// <summary>
    /// Index of the work area that holds most of the group, or -1. <paramref name="intersectingCount"/>
    /// tells whether the group straddles a seam (two or more work areas).
    /// </summary>
    internal static int FindDominantWorkArea(DesktopPixelRect window, IReadOnlyList<DesktopPixelRect> workAreas, out int intersectingCount)
    {
        intersectingCount = 0;
        var best = -1;
        long bestArea = 0;
        for (var i = 0; i < workAreas.Count; i++)
        {
            var work = workAreas[i];
            long width = Math.Min(window.Right, work.Right) - Math.Max(window.Left, work.Left);
            long height = Math.Min(window.Bottom, work.Bottom) - Math.Max(window.Top, work.Top);
            if (width <= 0 || height <= 0) continue;
            intersectingCount++;
            var area = width * height;
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Pixel shift that brings the group fully inside <paramref name="work"/> (left/top win when it is larger).</summary>
    internal static (int Dx, int Dy) FitInside(DesktopPixelRect window, DesktopPixelRect work)
    {
        static int Shift(int start, int end, int min, int max)
        {
            if (end - start >= max - min) return min - start;
            if (start < min) return min - start;
            if (end > max) return max - end;
            return 0;
        }
        return (Shift(window.Left, window.Right, work.Left, work.Right), Shift(window.Top, window.Bottom, work.Top, work.Bottom));
    }
}

internal readonly record struct GroupPlacementRect(double Left, double Top, double Width, double Height)
{
    internal double Right => Left + Width;
    internal double Bottom => Top + Height;

    internal bool IntersectsWithGap(GroupPlacementRect other, double gap)
        => Left < other.Right + gap && other.Left < Right + gap &&
           Top < other.Bottom + gap && other.Top < Bottom + gap;
}

internal readonly record struct GroupPlacementResult(double X, double Y, bool Overlaps);

/// <summary>
/// Places a new group at a visible free spot of one monitor work area (all values in the
/// layout's per-monitor DIPs). With an anchor the nearest free spot wins; without one the
/// top-left-most free spot wins. When nothing is free the group is cascaded and may overlap.
/// </summary>
internal static class GroupPlacementPolicy
{
    internal const double Margin = 24;
    internal const double Step = 24;

    internal static GroupPlacementResult FindFreeSpot(
        GroupPlacementRect work,
        double width,
        double height,
        IReadOnlyList<GroupPlacementRect> occupied,
        double gap,
        double? anchorX = null,
        double? anchorY = null,
        int cascadeIndex = 0)
    {
        width = double.IsFinite(width) && width > 0 ? width : 360;
        height = double.IsFinite(height) && height > 0 ? height : 310;
        gap = double.IsFinite(gap) ? Math.Clamp(gap, 0, 64) : 12;
        var minX = work.Left + Margin;
        var minY = work.Top + Margin;
        var maxX = Math.Max(minX, work.Right - Margin - width);
        var maxY = Math.Max(minY, work.Bottom - Margin - height);

        var xs = Axis(minX, maxX);
        var ys = Axis(minY, maxY);
        var candidates = new List<(double X, double Y)>(xs.Count * ys.Count);
        foreach (var y in ys)
            foreach (var x in xs)
                candidates.Add((x, y));

        IEnumerable<(double X, double Y)> ordered = candidates;
        if (anchorX is double ax && anchorY is double ay && double.IsFinite(ax) && double.IsFinite(ay))
        {
            ordered = candidates.OrderBy(c =>
            {
                var dx = c.X + width / 2 - ax;
                var dy = c.Y + height / 2 - ay;
                return dx * dx + dy * dy;
            });
        }

        foreach (var (x, y) in ordered)
        {
            var rect = new GroupPlacementRect(x, y, width, height);
            if (!occupied.Any(o => rect.IntersectsWithGap(o, gap)))
                return new GroupPlacementResult(x, y, false);
        }

        var offset = (Math.Abs(cascadeIndex) % 8) * 18;
        return new GroupPlacementResult(
            Math.Min(minX + 76 + offset, maxX),
            Math.Min(minY + 76 + offset, maxY),
            true);
    }

    private static List<double> Axis(double min, double max)
    {
        var values = new List<double>();
        for (var value = min; value <= max + 0.01 && values.Count < 400; value += Step) values.Add(value);
        if (values.Count == 0 || values[^1] < max - 0.01) values.Add(max);
        return values;
    }
}

/// <summary>Where a group goes when it is sent to another desktop page from its menu.</summary>
internal static class GroupPageSendPolicy
{
    /// <summary>Existing pages plus exactly one new page after the last one.</summary>
    internal static int ClampTarget(int requestedPage, int maxPage)
        => Math.Clamp(requestedPage, 0, Math.Max(0, maxPage) + 1);

    /// <summary>
    /// Page for "새 페이지로 보내기": one after the highest page of the other groups
    /// (<paramref name="maxOtherPage"/> is -1 when there are none). Returns -1 when that is the
    /// page the group is already alone on, so sending it would only leave an empty page behind.
    /// </summary>
    internal static int NewPageTarget(int ownPage, bool ownShowOnAllPages, int maxOtherPage)
    {
        var target = Math.Max(-1, maxOtherPage) + 1;
        return !ownShowOnAllPages && ownPage == target ? -1 : target;
    }

    /// <summary>Follow when asked to, or when the page the user is looking at became empty.</summary>
    internal static bool ShouldFollow(bool followPreference, int currentPage, int targetPage, int groupsLeftOnCurrentPage)
        => targetPage != currentPage && (followPreference || groupsLeftOnCurrentPage <= 0);
}

/// <summary>Templates are stored newest first; saving inserts at the front and trims the tail.</summary>
internal static class GroupTemplateLimitPolicy
{
    internal const int MaxTemplates = 50;

    internal static IReadOnlyList<T> EvictedBySave<T>(IReadOnlyList<T> newestFirst, T? replacing, int maxTemplates = MaxTemplates)
        where T : class
    {
        var remaining = newestFirst.Where(t => !ReferenceEquals(t, replacing)).ToList();
        var keep = Math.Max(0, maxTemplates - 1);
        return remaining.Count > keep ? remaining.Skip(keep).ToList() : Array.Empty<T>();
    }
}
