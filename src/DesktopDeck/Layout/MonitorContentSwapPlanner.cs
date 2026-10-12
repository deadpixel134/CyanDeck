using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopDeck.Layout;

/// <summary>
/// Pure geometry planner for swapping the desktop contents of two monitors.
/// The planner works only in physical pixels. It never enumerates monitors,
/// mutates DesktopDeck state, writes files, or touches WPF windows.
/// </summary>
public sealed class MonitorContentSwapPlanner
{
    public MonitorContentSwapPlan CreatePlan(
        IReadOnlyList<MonitorSwapDescriptor> monitors,
        IReadOnlyList<ScreenContentPlacement> placements,
        string firstMonitorId,
        string secondMonitorId,
        MonitorContentSwapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(placements);

        if (string.IsNullOrWhiteSpace(firstMonitorId) || string.IsNullOrWhiteSpace(secondMonitorId))
        {
            throw new ArgumentException("Both monitor IDs are required.");
        }

        if (string.Equals(firstMonitorId, secondMonitorId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The two monitors must be different.");
        }

        var normalized = (options ?? new MonitorContentSwapOptions()).Normalize();
        var monitorMap = BuildMonitorMap(monitors);
        if (!monitorMap.TryGetValue(firstMonitorId, out var first) ||
            !monitorMap.TryGetValue(secondMonitorId, out var second))
        {
            throw new ArgumentException("Both selected monitors must exist in the current topology.");
        }

        var seenItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<ScreenContentSwapMove>();
        var unchanged = new List<string>();
        var groupCount = 0;
        var iconCount = 0;

        foreach (var placement in placements)
        {
            if (!placement.IsValid)
            {
                throw new ArgumentException("Every placement must have a valid ID, monitor ID, and physical rectangle.");
            }

            if (!seenItems.Add(placement.ItemId))
            {
                throw new ArgumentException($"Duplicate placement ID: {placement.ItemId}");
            }

            var onFirst = string.Equals(placement.MonitorId, first.MonitorId, StringComparison.OrdinalIgnoreCase);
            var onSecond = string.Equals(placement.MonitorId, second.MonitorId, StringComparison.OrdinalIgnoreCase);
            if (!onFirst && !onSecond)
            {
                unchanged.Add(placement.ItemId);
                continue;
            }

            if (placement.Kind == ScreenContentKind.Group && placement.IsLocked && !normalized.IncludeLockedGroups)
            {
                unchanged.Add(placement.ItemId);
                continue;
            }

            var source = onFirst ? first : second;
            var target = onFirst ? second : first;
            var after = MapRect(placement.BoundsPx, source.WorkAreaPx, target.WorkAreaPx, normalized);

            moves.Add(new ScreenContentSwapMove(
                placement.ItemId,
                placement.Kind,
                source.MonitorId,
                target.MonitorId,
                placement.BoundsPx,
                after,
                placement.IsLocked));

            if (placement.Kind == ScreenContentKind.Group)
            {
                groupCount++;
            }
            else if (placement.Kind == ScreenContentKind.LooseIcon)
            {
                iconCount++;
            }
        }

        return new MonitorContentSwapPlan(
            first.MonitorId,
            second.MonitorId,
            moves,
            unchanged,
            groupCount,
            iconCount);
    }

    public static PhysicalRect MapRect(
        PhysicalRect item,
        PhysicalRect sourceWorkArea,
        PhysicalRect targetWorkArea,
        MonitorContentSwapOptions? options = null)
    {
        if (!item.IsValid || !sourceWorkArea.IsValid || !targetWorkArea.IsValid)
        {
            throw new ArgumentException("Item and work-area rectangles must be finite and non-empty.");
        }

        var normalized = (options ?? new MonitorContentSwapOptions()).Normalize();
        var source = Inset(sourceWorkArea, normalized.EdgeInsetPx);
        var target = Inset(targetWorkArea, normalized.EdgeInsetPx);
        if (!source.IsValid || !target.IsValid)
        {
            throw new ArgumentException("The configured edge inset leaves no usable working area.");
        }

        var relativeX = (item.X - source.X) / source.Width;
        var relativeY = (item.Y - source.Y) / source.Height;

        double width;
        double height;
        if (normalized.ScaleSizeWithWorkingArea)
        {
            width = item.Width * (target.Width / source.Width);
            height = item.Height * (target.Height / source.Height);
        }
        else
        {
            width = item.Width;
            height = item.Height;
        }

        width = Math.Clamp(width, 1d, target.Width);
        height = Math.Clamp(height, 1d, target.Height);

        var x = target.X + (relativeX * target.Width);
        var y = target.Y + (relativeY * target.Height);

        x = Math.Clamp(x, target.Left, target.Right - width);
        y = Math.Clamp(y, target.Top, target.Bottom - height);

        return new PhysicalRect(x, y, width, height);
    }

    private static Dictionary<string, MonitorSwapDescriptor> BuildMonitorMap(IReadOnlyList<MonitorSwapDescriptor> monitors)
    {
        var result = new Dictionary<string, MonitorSwapDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in monitors)
        {
            if (!monitor.IsValid)
            {
                throw new ArgumentException("Every monitor descriptor must be valid.");
            }

            if (!result.TryAdd(monitor.MonitorId, monitor))
            {
                throw new ArgumentException($"Duplicate monitor ID: {monitor.MonitorId}");
            }
        }

        if (result.Count < 2)
        {
            throw new ArgumentException("At least two monitors are required to swap screen contents.");
        }

        return result;
    }

    private static PhysicalRect Inset(PhysicalRect rect, double inset)
    {
        if (inset <= 0d)
        {
            return rect;
        }

        return new PhysicalRect(
            rect.X + inset,
            rect.Y + inset,
            rect.Width - (inset * 2d),
            rect.Height - (inset * 2d));
    }
}
