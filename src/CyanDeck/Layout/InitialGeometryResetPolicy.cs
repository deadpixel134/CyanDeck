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
        var inputs = groups.Select(g => new MonitorGroupRecoveryPlanner.Group(
            g.Id, g.PageIndex, g.ShowOnAllPages, w, h)).ToArray();
        return MonitorGroupRecoveryPlanner.Plan(primaryWorkArea, inputs, gap);
    }
}
