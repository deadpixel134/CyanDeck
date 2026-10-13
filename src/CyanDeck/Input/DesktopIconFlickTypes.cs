using System;

namespace CyanDeck.Input;

internal enum DesktopIconFlickUpdateKind
{
    None = 0,
    Tracking,
    Armed,
    Preview,
    Committed,
    Cancelled,
}

internal enum DesktopIconFlickCancelReason
{
    None = 0,
    Disabled,
    InvalidSample,
    IneligibleSource,
    BlockedSource,
    MultiTouch,
    Timeout,
    InsufficientTravel,
    InsufficientVelocity,
    NoTarget,
    MoveRejected,
    LostCapture,
    LeftSurface,
    Disposed,
}

internal readonly record struct DesktopIconFlickSample(
    int TouchId,
    double X,
    double Y,
    long TimestampMilliseconds)
{
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

internal readonly record struct DesktopIconFlickSurface(double Width, double Height)
{
    internal bool IsValid =>
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;

    internal bool Contains(DesktopIconFlickSample sample) =>
        sample.X >= 0 && sample.Y >= 0 && sample.X <= Width && sample.Y <= Height;
}

internal readonly record struct DesktopIconFlickVector(double X, double Y)
{
    internal double Magnitude => Math.Sqrt((X * X) + (Y * Y));
}

internal readonly record struct DesktopIconFlickUpdate(
    DesktopIconFlickUpdateKind Kind,
    double TravelDip,
    DesktopIconFlickVector VelocityDipPerSecond,
    DesktopIconFlickCancelReason CancelReason)
{
    internal static DesktopIconFlickUpdate None =>
        new(DesktopIconFlickUpdateKind.None, 0, default, DesktopIconFlickCancelReason.None);

    internal static DesktopIconFlickUpdate Cancelled(
        double travelDip,
        DesktopIconFlickVector velocity,
        DesktopIconFlickCancelReason reason) =>
        new(DesktopIconFlickUpdateKind.Cancelled, travelDip, velocity, reason);
}

internal readonly record struct DesktopIconFlickOptions(
    bool Enabled = false,
    double ActivationDistanceDip = 10,
    double MinimumCommitTravelDip = 48,
    double MinimumVelocityDipPerSecond = 650,
    int VelocityWindowMilliseconds = 120,
    int MaximumGestureDurationMilliseconds = 650,
    double TargetExpansionDip = 18,
    double MaximumProjectionDistanceDip = 1200)
{
    internal DesktopIconFlickOptions Normalize() => new(
        Enabled,
        ClampFinite(ActivationDistanceDip, 4, 40, 10),
        ClampFinite(MinimumCommitTravelDip, 24, 240, 48),
        ClampFinite(MinimumVelocityDipPerSecond, 250, 3000, 650),
        Math.Clamp(VelocityWindowMilliseconds, 40, 300),
        Math.Clamp(MaximumGestureDurationMilliseconds, 150, 1500),
        ClampFinite(TargetExpansionDip, 0, 80, 18),
        ClampFinite(MaximumProjectionDistanceDip, 120, 4000, 1200));

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

internal readonly record struct DesktopIconFlickRect(double Left, double Top, double Right, double Bottom)
{
    internal bool IsValid =>
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Right) && double.IsFinite(Bottom) &&
        Right > Left && Bottom > Top;

    internal DesktopIconFlickRect Inflate(double amount) => new(
        Left - amount,
        Top - amount,
        Right + amount,
        Bottom + amount);

    internal bool Contains(double x, double y) =>
        x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// Stable host-owned source identity. The overlay intentionally knows nothing about files,
/// Shell PIDLs, collection models, or Folder Portal paths.
/// </summary>
internal readonly record struct DesktopIconFlickSource(string StableId)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(StableId);
}

/// <summary>
/// A host-owned collection target. TargetStableId should normally identify the destination
/// group/tab pair. Folder Portal targets should not be supplied unless the host deliberately
/// wants filesystem semantics; the recommended v7.6 integration supplies Collection targets only.
/// </summary>
internal readonly record struct DesktopIconFlickTarget(
    string TargetStableId,
    DesktopIconFlickRect Bounds)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(TargetStableId) && Bounds.IsValid;
}

internal readonly record struct DesktopIconFlickTargetHit(
    DesktopIconFlickTarget Target,
    double ProjectionDistanceDip);
