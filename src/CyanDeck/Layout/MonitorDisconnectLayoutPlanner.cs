using System;
using System.Collections.Generic;
using System.Linq;

namespace CyanDeck.Layout;

/// <summary>
/// Pure, deterministic topology-shrink planner. Existing on-screen groups stay
/// at exactly their saved coordinates; only missing/off-screen groups are placed
/// into free space. Groups on different desktop pages may reuse space, while
/// show-on-all-pages groups are obstacles on every page.
/// All geometry uses WPF device-independent units (DIPs).
/// </summary>
internal static class MonitorDisconnectLayoutPlanner
{
    internal readonly record struct SurvivingArea(string DeviceName, double X, double Y,
        double Width, double Height, bool Primary)
    {
        internal double Right => X + Width;
        internal double Bottom => Y + Height;
        internal bool IsValid => !string.IsNullOrWhiteSpace(DeviceName) &&
            double.IsFinite(X) && double.IsFinite(Y) &&
            double.IsFinite(Width) && double.IsFinite(Height) &&
            Width > 0 && Height > 0;
    }

    internal readonly record struct DisconnectedGroup(Guid Id, int PageIndex, bool ShowOnAllPages,
        double X, double Y, double Width, double Height);
    internal readonly record struct RehomePlacement(Guid Id, double X, double Y,
        double Width, double Height, bool Relocated);
    internal sealed record RehomeResult(IReadOnlyList<RehomePlacement> Placements,
        int RelocatedCount, bool OverlapUnavoidable);

    internal static bool IsMonitorReduction(int previousCount, int currentCount) =>
        previousCount > currentCount && currentCount > 0;

    internal static RehomeResult Plan(IReadOnlyList<SurvivingArea> remaining,
        IReadOnlyList<DisconnectedGroup> groups, double requestedGap = 12)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentNullException.ThrowIfNull(groups);
        if (remaining.Count == 0 || remaining.Any(m => !m.IsValid))
            throw new ArgumentException("At least one valid monitor work area is required.", nameof(remaining));
        if (groups.Any(g => g.Id == Guid.Empty || !double.IsFinite(g.X) ||
            !double.IsFinite(g.Y) || !double.IsFinite(g.Width) ||
            !double.IsFinite(g.Height) || g.Width <= 0 || g.Height <= 0) ||
            groups.Select(g => g.Id).Distinct().Count() != groups.Count)
            throw new ArgumentException("Groups must have unique IDs and finite positive rectangles.", nameof(groups));

        var gap = Math.Clamp(double.IsFinite(requestedGap) ? requestedGap : 12, 0, 24);
        var result = new List<RehomePlacement>(groups.Count);
        var occupied = new List<(DisconnectedGroup DisconnectedGroup, RehomePlacement RehomePlacement)>(groups.Count);
        var toMove = new List<DisconnectedGroup>();

        foreach (var group in groups)
        {
            if (remaining.Any(area => FullyWithin(group, area)))
            {
                var keep = new RehomePlacement(group.Id, group.X, group.Y,
                    group.Width, group.Height, false);
                result.Add(keep);
                occupied.Add((group, keep));
            }
            else toMove.Add(group);
        }

        var unavoidable = false;
        // Global groups must be allocated first so they cannot collide on a
        // different page that was not considered when arranging local groups.
        foreach (var group in toMove.OrderBy(g => g.ShowOnAllPages ? 0 : 1)
                                    .ThenBy(g => Math.Max(0, g.PageIndex))
                                    .ThenBy(g => g.Id))
        {
            RehomePlacement? bestClear = null;
            RehomePlacement? bestFallback = null;
            double bestClearScore = double.PositiveInfinity;
            double bestFallbackScore = double.PositiveInfinity;
            foreach (var area in remaining)
            {
                var margin = Math.Min(gap, Math.Min(area.Width, area.Height) / 50);
                var width = Math.Min(group.Width, Math.Max(1d, area.Width - 2 * margin));
                var height = Math.Min(group.Height, Math.Max(1d, area.Height - 2 * margin));
                var minX = area.X + margin;
                var maxX = Math.Max(minX, area.Right - margin - width);
                var minY = area.Y + margin;
                var maxY = Math.Max(minY, area.Bottom - margin - height);
                var preferredX = Math.Clamp(group.X, minX, maxX);
                var preferredY = Math.Clamp(group.Y, minY, maxY);

                var xCandidates = new SortedSet<double> { minX, maxX, preferredX };
                var yCandidates = new SortedSet<double> { minY, maxY, preferredY };
                foreach (var obstacle in occupied)
                {
                    if (!Conflicts(group, obstacle.DisconnectedGroup)) continue;
                    var rect = obstacle.RehomePlacement;
                    xCandidates.Add(Math.Clamp(rect.X + rect.Width + gap, minX, maxX));
                    xCandidates.Add(Math.Clamp(rect.X - width - gap, minX, maxX));
                    xCandidates.Add(Math.Clamp(rect.X + rect.Width, minX, maxX));
                    yCandidates.Add(Math.Clamp(rect.Y + rect.Height + gap, minY, maxY));
                    yCandidates.Add(Math.Clamp(rect.Y - height - gap, minY, maxY));
                    yCandidates.Add(Math.Clamp(rect.Y + rect.Height, minY, maxY));
                }

                // Prefer the nearest surviving monitor to the old group's
                // centre; on a one-monitor system all moved groups land there.
                var cx = group.X + group.Width / 2;
                var cy = group.Y + group.Height / 2;
                var monitorDistance = Math.Abs(cx - Math.Clamp(cx, area.X, area.Right)) +
                    Math.Abs(cy - Math.Clamp(cy, area.Y, area.Bottom));
                foreach (var x in xCandidates)
                foreach (var y in yCandidates)
                {
                    var candidate = new RehomePlacement(group.Id, x, y, width, height, true);
                    var score = monitorDistance * 2 + Math.Abs(x - preferredX) +
                        Math.Abs(y - preferredY) + y * 0.000001 + x * 0.0000001;
                    var overlapArea = occupied
                        .Where(o => Conflicts(group, o.DisconnectedGroup))
                        .Sum(o => IntersectionArea(candidate, o.RehomePlacement));
                    if (overlapArea <= .000001 && score < bestClearScore)
                    {
                        bestClear = candidate;
                        bestClearScore = score;
                    }
                    // Limited screen space: choose the *least* overlap rather
                    // than stack every missing monitor group at the same edge.
                    var fallbackScore = overlapArea * 1000d + score;
                    if (fallbackScore < bestFallbackScore)
                    {
                        bestFallback = candidate;
                        bestFallbackScore = fallbackScore;
                    }
                }
            }
            var selected = bestClear ?? bestFallback ??
                throw new InvalidOperationException("No valid target monitor placement.");
            if (bestClear is null) unavoidable = true;
            result.Add(selected);
            occupied.Add((group, selected));
        }
        return new RehomeResult(result, toMove.Count, unavoidable);
    }

    private static bool FullyWithin(DisconnectedGroup g, SurvivingArea area)
    {
        const double tolerance = 1d;
        return g.X >= area.X - tolerance && g.Y >= area.Y - tolerance &&
            g.X + g.Width <= area.Right + tolerance &&
            g.Y + g.Height <= area.Bottom + tolerance;
    }

    private static bool Conflicts(DisconnectedGroup left, DisconnectedGroup right) =>
        left.ShowOnAllPages || right.ShowOnAllPages ||
        Math.Max(0, left.PageIndex) == Math.Max(0, right.PageIndex);

    private static double IntersectionArea(RehomePlacement a, RehomePlacement b) =>
        Math.Max(0d, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
        Math.Max(0d, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
}
