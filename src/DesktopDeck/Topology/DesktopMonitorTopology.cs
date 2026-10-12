using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopDeck.Topology;

/// <summary>
/// Immutable, session-local topology snapshot for Desktop Pages.
/// Physical-pixel monitor rectangles are used only to classify monitor seams.
/// Page animation distances remain local WPF DIPs on the owning surface.
/// </summary>
internal sealed class DesktopMonitorTopology
{
    private readonly IReadOnlyList<DesktopMonitorSurfaceMetric> _surfaces;
    private readonly Dictionary<string, DesktopMonitorSurfaceMetric> _bySurfaceId;

    internal DesktopMonitorTopology(long revision, IEnumerable<DesktopMonitorSurfaceMetric> surfaces)
    {
        Revision = revision;
        _surfaces = (surfaces ?? Array.Empty<DesktopMonitorSurfaceMetric>())
            .Where(static metric => metric.IsValid)
            .GroupBy(static metric => metric.SurfaceId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        _bySurfaceId = _surfaces.ToDictionary(
            static metric => metric.SurfaceId,
            StringComparer.Ordinal);
    }

    internal long Revision { get; }
    internal IReadOnlyList<DesktopMonitorSurfaceMetric> Surfaces => _surfaces;

    internal bool TryGetSurface(string surfaceId, out DesktopMonitorSurfaceMetric metric) =>
        _bySurfaceId.TryGetValue(surfaceId, out metric);

    internal DesktopMonitorEdgeClassification ClassifyHorizontalEdge(
        string surfaceId,
        DesktopMonitorHorizontalEdge edge,
        double localYDip,
        DesktopMonitorTopologyOptions? options = null)
    {
        options = (options ?? new DesktopMonitorTopologyOptions()).Normalize();

        if (!TryGetSurface(surfaceId, out DesktopMonitorSurfaceMetric current))
        {
            return DesktopMonitorEdgeClassification.Invalid;
        }

        double screenY = current.LocalYDipToScreenPixel(localYDip);
        return ClassifyHorizontalEdgeAtScreenY(surfaceId, edge, screenY, options);
    }

    internal DesktopMonitorEdgeClassification ClassifyHorizontalEdgeAtScreenY(
        string surfaceId,
        DesktopMonitorHorizontalEdge edge,
        double screenY,
        DesktopMonitorTopologyOptions? options = null)
    {
        options = (options ?? new DesktopMonitorTopologyOptions()).Normalize();

        if (!TryGetSurface(surfaceId, out DesktopMonitorSurfaceMetric current) ||
            !double.IsFinite(screenY) ||
            !current.MonitorBoundsPx.ContainsY(screenY))
        {
            return DesktopMonitorEdgeClassification.Invalid;
        }

        EdgeCandidate? nearestDetached = null;
        EdgeCandidate? seam = null;

        foreach (DesktopMonitorSurfaceMetric other in _surfaces)
        {
            if (string.Equals(other.SurfaceId, current.SurfaceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!other.MonitorBoundsPx.ContainsY(screenY))
            {
                continue;
            }

            if (!TryGetHorizontalGap(current.MonitorBoundsPx, other.MonitorBoundsPx, edge, out int gap))
            {
                continue;
            }

            var candidate = new EdgeCandidate(other.MonitorKey, gap);
            if (gap <= options.AdjacentGapTolerancePx)
            {
                if (seam is null || candidate.GapPx < seam.Value.GapPx)
                {
                    seam = candidate;
                }
            }
            else if (gap <= options.DetachedGapClassificationLimitPx &&
                     (nearestDetached is null || candidate.GapPx < nearestDetached.Value.GapPx))
            {
                nearestDetached = candidate;
            }
        }

        if (seam is EdgeCandidate seamCandidate)
        {
            return new DesktopMonitorEdgeClassification(
                DesktopMonitorEdgeKind.InteriorSeam,
                options.AllowInteriorSeamPageNavigation
                    ? DesktopPageEdgeNavigationDecision.AllowedInteriorSeam
                    : DesktopPageEdgeNavigationDecision.BlockedInteriorSeam,
                seamCandidate.MonitorKey,
                seamCandidate.GapPx,
                screenY);
        }

        if (nearestDetached is EdgeCandidate detachedCandidate)
        {
            return new DesktopMonitorEdgeClassification(
                DesktopMonitorEdgeKind.DetachedGap,
                DesktopPageEdgeNavigationDecision.AllowedDetachedEdge,
                detachedCandidate.MonitorKey,
                detachedCandidate.GapPx,
                screenY);
        }

        return new DesktopMonitorEdgeClassification(
            DesktopMonitorEdgeKind.OuterDesktop,
            DesktopPageEdgeNavigationDecision.AllowedOuterEdge,
            null,
            0,
            screenY);
    }

    internal bool IsSurfaceDpiSynchronized(string surfaceId, DesktopMonitorTopologyOptions? options = null)
    {
        options = (options ?? new DesktopMonitorTopologyOptions()).Normalize();
        return TryGetSurface(surfaceId, out DesktopMonitorSurfaceMetric metric) &&
               !metric.HasDpiMismatch(options.DpiChangeTolerance);
    }

    internal DesktopPageTopologyToken CreateToken(string surfaceId)
    {
        if (!TryGetSurface(surfaceId, out DesktopMonitorSurfaceMetric metric))
        {
            return default;
        }

        return new DesktopPageTopologyToken(
            Revision,
            metric.SurfaceId,
            metric.MonitorKey,
            metric.SurfaceBoundsPx,
            metric.WindowDpi,
            metric.WpfDpiX,
            metric.WpfDpiY);
    }

    internal DesktopPageTopologyStability EvaluateStability(
        DesktopPageTopologyToken token,
        DesktopMonitorTopologyOptions? options = null)
    {
        options = (options ?? new DesktopMonitorTopologyOptions()).Normalize();

        if (string.IsNullOrWhiteSpace(token.SurfaceId) ||
            !TryGetSurface(token.SurfaceId, out DesktopMonitorSurfaceMetric current))
        {
            return DesktopPageTopologyStability.CancelSurfaceMissing;
        }

        if (current.HasDpiMismatch(options.DpiChangeTolerance) ||
            !string.Equals(token.MonitorKey, current.MonitorKey, StringComparison.Ordinal) ||
            Math.Abs((long)token.WindowDpi - current.WindowDpi) > options.DpiChangeTolerance ||
            Math.Abs(token.WpfDpiX - current.WpfDpiX) > options.DpiChangeTolerance ||
            Math.Abs(token.WpfDpiY - current.WpfDpiY) > options.DpiChangeTolerance ||
            !RectNearlyEquals(token.SurfaceBoundsPx, current.SurfaceBoundsPx, options.SurfaceBoundsTolerancePx))
        {
            return DesktopPageTopologyStability.Replan;
        }

        return token.Revision == Revision
            ? DesktopPageTopologyStability.Stable
            : DesktopPageTopologyStability.Replan;
    }

    private static bool TryGetHorizontalGap(
        DesktopPixelRect current,
        DesktopPixelRect other,
        DesktopMonitorHorizontalEdge edge,
        out int gap)
    {
        if (edge == DesktopMonitorHorizontalEdge.Left)
        {
            if (other.Left >= current.Left || other.Right > current.Left)
            {
                gap = 0;
                return false;
            }

            gap = current.Left - other.Right;
            return gap >= 0;
        }

        if (other.Right <= current.Right || other.Left < current.Right)
        {
            gap = 0;
            return false;
        }

        gap = other.Left - current.Right;
        return gap >= 0;
    }

    private static bool RectNearlyEquals(DesktopPixelRect a, DesktopPixelRect b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance &&
        Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Right - b.Right) <= tolerance &&
        Math.Abs(a.Bottom - b.Bottom) <= tolerance;

    private readonly record struct EdgeCandidate(string MonitorKey, int GapPx);
}
