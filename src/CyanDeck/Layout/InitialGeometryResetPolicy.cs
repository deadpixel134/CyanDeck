using System;
using System.Collections.Generic;
using System.Linq;

namespace CyanDeck.Layout;

/// <summary>
/// Reset only visual geometry using the existing, monitor-aware arrangement
/// planner. Identifiers, memberships, tabs, items, rules and page assignments
/// are never input to mutation and must remain untouched by callers.
/// </summary>
internal static class InitialGeometryResetPolicy
{
    internal readonly record struct ExistingGroup(Guid Id, int PageIndex, bool ShowOnAllPages);
    internal static readonly (double Left, double Top, double Width, double Height) CalendarDefaults =
        (120, 110, 650, 510);

    internal static MonitorGroupRecoveryPlanner.PlanResult Plan(
        MonitorGroupRecoveryPlanner.WorkArea primaryWorkArea,
        IReadOnlyList<ExistingGroup> groups, double gap)
    {
        ArgumentNullException.ThrowIfNull(groups);
        // Match the first-launch CreateDefaultLayout dimensions (DIP), not
        // whatever sizes the user happened to drag the groups into later.
        var w = Math.Min(365d, Math.Max(220d, (primaryWorkArea.Width - 72d - 36d) / 3d));
        var h = Math.Min(355d, Math.Max(240d, primaryWorkArea.Height / 2.5d));
        // On an unchanged four-group first-launch configuration, reproduce
        // CreateDefaultLayout's exact initial geometry (3 groups on the first
        // row, system group below the third). Never recreate their contents.
        var initialH = Math.Min(260d, h);
        var canFitDefault = primaryWorkArea.Width >= 72d + 3d * w + 36d &&
            primaryWorkArea.Height >= 46d + h + 18d + initialH;
        if (canFitDefault && groups.Count is >= 1 and <= 4 &&
            groups.All(g => g.PageIndex == 0 && !g.ShowOnAllPages))
        {
            var left = primaryWorkArea.X + 36d;
            var top = primaryWorkArea.Y + 46d;
            var slots = new[]
            {
                (left, top, w, h),
                (left + w + 18d, top, w, h),
                (left + (w + 18d) * 2d, top, w, h),
                (left + (w + 18d) * 2d, top + h + 18d, w, initialH)
            };
            var placements = groups.Select((g, i) => new MonitorGroupRecoveryPlanner.Placement(
                g.Id, slots[i].Item1, slots[i].Item2,
                slots[i].Item3, slots[i].Item4)).ToArray();
            return new MonitorGroupRecoveryPlanner.PlanResult(placements, false);
        }
        // Custom groups and very small displays use collision-aware packing.
        var inputs = groups.Select(g => new MonitorGroupRecoveryPlanner.Group(
            g.Id, g.PageIndex, g.ShowOnAllPages, w, h)).ToArray();
        return MonitorGroupRecoveryPlanner.Plan(primaryWorkArea, inputs, gap);
    }
}
