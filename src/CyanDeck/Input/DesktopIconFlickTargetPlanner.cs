using System;
using System.Collections.Generic;

namespace CyanDeck.Input;

/// <summary>
/// Selects the first eligible destination rectangle hit by a ray projected from the release
/// point in the measured flick-velocity direction. All geometry is host-supplied surface-local DIP.
/// </summary>
internal static class DesktopIconFlickTargetPlanner
{
    internal static bool TrySelectTarget(
        double startX,
        double startY,
        double releaseX,
        double releaseY,
        DesktopIconFlickVector velocityDipPerSecond,
        IReadOnlyList<DesktopIconFlickTarget> targets,
        DesktopIconFlickOptions options,
        out DesktopIconFlickTargetHit hit)
    {
        hit = default;
        options = options.Normalize();

        if (!double.IsFinite(startX) || !double.IsFinite(startY) ||
            !double.IsFinite(releaseX) || !double.IsFinite(releaseY) ||
            targets is null || targets.Count == 0)
        {
            return false;
        }

        double magnitude = velocityDipPerSecond.Magnitude;
        if (!double.IsFinite(magnitude) || magnitude <= double.Epsilon)
        {
            return false;
        }

        double dx = velocityDipPerSecond.X / magnitude;
        double dy = velocityDipPerSecond.Y / magnitude;
        double bestDistance = double.PositiveInfinity;
        DesktopIconFlickTarget best = default;
        bool found = false;

        foreach (DesktopIconFlickTarget target in targets)
        {
            if (!target.IsValid)
            {
                continue;
            }

            DesktopIconFlickRect expanded = target.Bounds.Inflate(options.TargetExpansionDip);
            if (expanded.Contains(startX, startY))
            {
                // Do not select the container the source already started inside.
                continue;
            }

            if (!TryRayRectIntersection(releaseX, releaseY, dx, dy, expanded, out double distance))
            {
                continue;
            }

            if (distance < 0 || distance > options.MaximumProjectionDistanceDip)
            {
                continue;
            }

            if (!found || distance < bestDistance ||
                (Math.Abs(distance - bestDistance) < 0.001 &&
                 string.CompareOrdinal(target.TargetStableId, best.TargetStableId) < 0))
            {
                found = true;
                bestDistance = distance;
                best = target;
            }
        }

        if (!found)
        {
            return false;
        }

        hit = new DesktopIconFlickTargetHit(best, bestDistance);
        return true;
    }

    private static bool TryRayRectIntersection(
        double ox,
        double oy,
        double dx,
        double dy,
        DesktopIconFlickRect rect,
        out double distance)
    {
        distance = 0;
        double tMin = 0;
        double tMax = double.PositiveInfinity;

        if (!ClipAxis(ox, dx, rect.Left, rect.Right, ref tMin, ref tMax) ||
            !ClipAxis(oy, dy, rect.Top, rect.Bottom, ref tMin, ref tMax))
        {
            return false;
        }

        distance = tMin;
        return double.IsFinite(distance) && distance >= 0;
    }

    private static bool ClipAxis(
        double origin,
        double direction,
        double min,
        double max,
        ref double tMin,
        ref double tMax)
    {
        const double Epsilon = 1e-9;
        if (Math.Abs(direction) < Epsilon)
        {
            return origin >= min && origin <= max;
        }

        double t1 = (min - origin) / direction;
        double t2 = (max - origin) / direction;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        tMin = Math.Max(tMin, t1);
        tMax = Math.Min(tMax, t2);
        return tMax >= tMin && tMax >= 0;
    }
}
