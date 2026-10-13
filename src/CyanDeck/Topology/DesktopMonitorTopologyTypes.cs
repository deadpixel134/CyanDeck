using System;

namespace CyanDeck.Topology;

internal enum DesktopMonitorHorizontalEdge
{
    Left = -1,
    Right = 1,
}

internal enum DesktopMonitorEdgeKind
{
    Invalid = 0,
    OuterDesktop,
    InteriorSeam,
    DetachedGap,
}

internal enum DesktopPageEdgeNavigationDecision
{
    Invalid = 0,
    AllowedOuterEdge,
    AllowedDetachedEdge,
    AllowedInteriorSeam,
    BlockedInteriorSeam,
}

internal enum DesktopPageTopologyStability
{
    Stable = 0,
    Replan,
    CancelSurfaceMissing,
}

internal readonly record struct DesktopPixelRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
    internal bool IsValid => Width > 0 && Height > 0;
    internal double CenterX => Left + (Width / 2.0);
    internal double CenterY => Top + (Height / 2.0);

    internal bool ContainsY(double y) => y >= Top && y < Bottom;

    internal static int VerticalOverlap(DesktopPixelRect a, DesktopPixelRect b) =>
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}

internal readonly record struct DesktopMonitorSurfaceMetric(
    string SurfaceId,
    string MonitorKey,
    DesktopPixelRect SurfaceBoundsPx,
    DesktopPixelRect MonitorBoundsPx,
    DesktopPixelRect WorkAreaPx,
    uint WindowDpi,
    double WpfDpiX,
    double WpfDpiY,
    bool IsPrimary)
{
    internal bool IsValid =>
        !string.IsNullOrWhiteSpace(SurfaceId) &&
        !string.IsNullOrWhiteSpace(MonitorKey) &&
        SurfaceBoundsPx.IsValid &&
        MonitorBoundsPx.IsValid &&
        WorkAreaPx.IsValid &&
        WindowDpi is >= 48 and <= 960 &&
        double.IsFinite(WpfDpiX) && WpfDpiX is >= 48 and <= 960 &&
        double.IsFinite(WpfDpiY) && WpfDpiY is >= 48 and <= 960;

    internal double SurfaceWidthDip =>
        IsValid ? SurfaceBoundsPx.Width * 96.0 / WpfDpiX : 0;

    internal double SurfaceHeightDip =>
        IsValid ? SurfaceBoundsPx.Height * 96.0 / WpfDpiY : 0;

    internal bool HasDpiMismatch(double toleranceDpi = 1.0) =>
        IsValid && Math.Abs(WindowDpi - WpfDpiX) > Math.Max(0, toleranceDpi);

    internal double LocalYDipToScreenPixel(double localYDip)
    {
        if (!IsValid || !double.IsFinite(localYDip))
        {
            return double.NaN;
        }

        double y = SurfaceBoundsPx.Top + (localYDip * WpfDpiY / 96.0);
        return Math.Clamp(y, SurfaceBoundsPx.Top, Math.Max(SurfaceBoundsPx.Top, SurfaceBoundsPx.Bottom - 1));
    }
}

internal sealed record DesktopMonitorTopologyOptions
{
    internal bool AllowInteriorSeamPageNavigation { get; init; } = false;
    internal int AdjacentGapTolerancePx { get; init; } = 8;
    internal int DetachedGapClassificationLimitPx { get; init; } = 256;
    internal int DpiChangeTolerance { get; init; } = 1;
    internal int SurfaceBoundsTolerancePx { get; init; } = 2;

    internal DesktopMonitorTopologyOptions Normalize() => this with
    {
        AdjacentGapTolerancePx = Math.Clamp(AdjacentGapTolerancePx, 0, 64),
        DetachedGapClassificationLimitPx = Math.Clamp(DetachedGapClassificationLimitPx, 16, 2048),
        DpiChangeTolerance = Math.Clamp(DpiChangeTolerance, 0, 24),
        SurfaceBoundsTolerancePx = Math.Clamp(SurfaceBoundsTolerancePx, 0, 32),
    };
}

internal readonly record struct DesktopMonitorEdgeClassification(
    DesktopMonitorEdgeKind Kind,
    DesktopPageEdgeNavigationDecision Decision,
    string? NeighborMonitorKey,
    int HorizontalGapPx,
    double ScreenYPx)
{
    internal static DesktopMonitorEdgeClassification Invalid => new(
        DesktopMonitorEdgeKind.Invalid,
        DesktopPageEdgeNavigationDecision.Invalid,
        null,
        0,
        double.NaN);
}

internal readonly record struct DesktopPageTopologyToken(
    long Revision,
    string SurfaceId,
    string MonitorKey,
    DesktopPixelRect SurfaceBoundsPx,
    uint WindowDpi,
    double WpfDpiX,
    double WpfDpiY);
