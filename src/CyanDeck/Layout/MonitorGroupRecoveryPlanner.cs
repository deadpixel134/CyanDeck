using System;
using System.Collections.Generic;
using System.Linq;

namespace CyanDeck.Layout;

/// <summary>
/// Plans a non-destructive, per-desktop-page recovery of every group onto one
/// monitor.  Groups displayed on all pages occupy reserved slots; separate
/// pages reuse the remaining slots so they need not compete for screen space.
/// Coordinates and dimensions are WPF device-independent units (DIPs).
/// </summary>
internal static class MonitorGroupRecoveryPlanner
{
    internal readonly record struct Group(Guid Id, int PageIndex, bool ShowOnAllPages,
        double Width, double Height);
    internal readonly record struct Placement(Guid Id, double X, double Y, double Width, double Height);
    internal readonly record struct WorkArea(double X, double Y, double Width, double Height);
    internal sealed record PlanResult(IReadOnlyList<Placement> Placements, bool OverlapUnavoidable);

    internal static PlanResult Plan(WorkArea work, IReadOnlyList<Group> groups, double requestedGap = 12)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (!double.IsFinite(work.X) || !double.IsFinite(work.Y) ||
            !double.IsFinite(work.Width) || !double.IsFinite(work.Height) ||
            work.Width <= 0 || work.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(work));
        if (groups.Select(g => g.Id).Distinct().Count() != groups.Count ||
            groups.Any(g => g.Id == Guid.Empty))
            throw new ArgumentException("Group identifiers must be nonempty and unique.", nameof(groups));
        if (groups.Count == 0) return new PlanResult(Array.Empty<Placement>(), false);

        var gap = Math.Clamp(double.IsFinite(requestedGap) ? requestedGap : 12, 6d, 22d);
        var margin = Math.Min(gap, Math.Min(work.Width, work.Height) * .025);
        var availableWidth = Math.Max(1d, work.Width - 2 * margin);
        var availableHeight = Math.Max(1d, work.Height - 2 * margin);

        var global = groups.Where(g => g.ShowOnAllPages).ToArray();
        var byPage = groups.Where(g => !g.ShowOnAllPages)
            .GroupBy(g => Math.Max(0, g.PageIndex)).ToArray();
        var simultaneous = global.Length + (byPage.Length == 0 ? 0 : byPage.Max(g => g.Count()));
        var count = Math.Max(1, simultaneous);

        // Optimize for legible, practical group rectangles. Oversubscribed
        // monitors fall back to overlapping rows with exposed headers rather
        // than positioning whole windows outside the usable work area.
        var columns = 1;
        var bestScore = double.PositiveInfinity;
        var maximumColumns = Math.Min(count, Math.Max(1, (int)Math.Floor((availableWidth + gap) / (220 + gap))));
        for (var candidate = 1; candidate <= maximumColumns; candidate++)
        {
            var rows = (int)Math.Ceiling(count / (double)candidate);
            var cellWidth = (availableWidth - gap * (candidate - 1)) / candidate;
            var cellHeight = (availableHeight - gap * (rows - 1)) / rows;
            var widthPenalty = Math.Max(0d, 240 - cellWidth) * 50;
            var heightPenalty = Math.Max(0d, 160 - cellHeight) * 50;
            var shapePenalty = Math.Abs(Math.Log(Math.Max(1, cellWidth) / 380)) +
                               Math.Abs(Math.Log(Math.Max(1, cellHeight) / 290));
            var unusedPenalty = (candidate * rows - count) * .09;
            var score = widthPenalty + heightPenalty + shapePenalty + unusedPenalty;
            if (score < bestScore) { bestScore = score; columns = candidate; }
        }

        var numberOfRows = (int)Math.Ceiling(count / (double)columns);
        var cellW = Math.Max(1d, (availableWidth - gap * (columns - 1)) / columns);
        var cellH = Math.Max(1d, (availableHeight - gap * (numberOfRows - 1)) / numberOfRows);
        var overlap = cellW < 220 || cellH < 140;
        var width = Math.Min(availableWidth, Math.Max(220, cellW));
        var height = Math.Min(availableHeight, Math.Max(140, cellH));

        // Every global group has one stable location. Per-page groups start at
        // the same offset so different pages never reduce each other's space.
        var placements = new List<Placement>(groups.Count);
        for (var i = 0; i < global.Length; i++) placements.Add(Place(global[i], i));
        foreach (var page in byPage)
        {
            var position = global.Length;
            foreach (var group in page) placements.Add(Place(group, position++));
        }
        return new PlanResult(placements, overlap);

        Placement Place(Group group, int slot)
        {
            var row = slot / columns;
            var column = slot % columns;
            var startX = work.X + margin + column * (cellW + gap);
            var startY = work.Y + margin + row * (cellH + gap);
            var itemW = Math.Min(width, Math.Max(220d, double.IsFinite(group.Width) ? group.Width : 360d));
            var itemH = Math.Min(height, Math.Max(140d, double.IsFinite(group.Height) ? group.Height : 310d));
            // If there is plenty of room, leave a group at its existing size;
            // otherwise shrink it to its allocated cell.
            itemW = Math.Min(itemW, Math.Max(220d, cellW));
            itemH = Math.Min(itemH, Math.Max(140d, cellH));
            var x = Math.Clamp(startX + Math.Max(0, cellW - itemW) / 2, work.X, Math.Max(work.X, work.X + work.Width - itemW));
            var y = Math.Clamp(startY + Math.Max(0, cellH - itemH) / 2, work.Y, Math.Max(work.Y, work.Y + work.Height - itemH));
            return new Placement(group.Id, x, y, itemW, itemH);
        }
    }
}
