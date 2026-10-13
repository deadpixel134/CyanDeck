using System;
using System.Collections.Generic;

namespace CyanDeck.Layout;

public readonly record struct PhysicalRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + (Width / 2d);
    public double CenterY => Y + (Height / 2d);
    public bool IsFinite =>
        double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height);
    public bool IsValid => IsFinite && Width > 0d && Height > 0d;
}

public sealed record MonitorSwapDescriptor(
    string MonitorId,
    string DisplayName,
    PhysicalRect BoundsPx,
    PhysicalRect WorkAreaPx,
    double DpiX,
    double DpiY,
    bool IsPrimary)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(MonitorId) &&
        BoundsPx.IsValid && WorkAreaPx.IsValid &&
        double.IsFinite(DpiX) && double.IsFinite(DpiY) &&
        DpiX >= 48d && DpiX <= 768d &&
        DpiY >= 48d && DpiY <= 768d;
}

public enum ScreenContentKind
{
    Group,
    LooseIcon,
    Other
}

public sealed record ScreenContentPlacement(
    string ItemId,
    string MonitorId,
    ScreenContentKind Kind,
    PhysicalRect BoundsPx,
    bool IsLocked = false)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ItemId) &&
        !string.IsNullOrWhiteSpace(MonitorId) &&
        BoundsPx.IsValid;
}

public sealed record MonitorContentSwapOptions
{
    public bool IncludeLockedGroups { get; init; } = true;
    public bool ScaleSizeWithWorkingArea { get; init; } = true;
    public double EdgeInsetPx { get; init; } = 0d;

    public MonitorContentSwapOptions Normalize() => this with
    {
        EdgeInsetPx = NormalizeInset(EdgeInsetPx)
    };

    private static double NormalizeInset(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 64d);
    }
}

public sealed record ScreenContentSwapMove(
    string ItemId,
    ScreenContentKind Kind,
    string SourceMonitorId,
    string TargetMonitorId,
    PhysicalRect BeforePx,
    PhysicalRect AfterPx,
    bool WasLocked);

public sealed record MonitorContentSwapPlan(
    string FirstMonitorId,
    string SecondMonitorId,
    IReadOnlyList<ScreenContentSwapMove> Moves,
    IReadOnlyList<string> UnchangedItemIds,
    int GroupMoveCount,
    int LooseIconMoveCount)
{
    public bool HasChanges => Moves.Count > 0;
}

public enum MonitorContentSwapStatus
{
    Success,
    NoChanges,
    InvalidRequest,
    CaptureFailed,
    DisplayUnstable,
    CheckpointFailed,
    ApplyFailed,
    RollbackFailed
}

public sealed record MonitorContentSwapResult(
    MonitorContentSwapStatus Status,
    string Message,
    MonitorContentSwapPlan? Plan = null,
    Exception? Error = null)
{
    public bool Succeeded => Status is MonitorContentSwapStatus.Success or MonitorContentSwapStatus.NoChanges;
}
