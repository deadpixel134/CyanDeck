using System;

namespace CyanDeck.Input;

internal enum DesktopPageTouchSwipeDirection
{
    Previous = -1,
    Next = 1,
}

internal enum DesktopPageTouchSwipeMode
{
    None = 0,
    OneFingerEdge,
    TwoFinger,
}

internal enum DesktopPageTouchSwipeUpdateKind
{
    None = 0,
    Started,
    Preview,
    Committed,
    Cancelled,
}

internal enum DesktopPageTouchSwipeCancelReason
{
    None = 0,
    Disabled,
    InvalidSurface,
    OutsideEdge,
    MultiTouch,
    PairJoinTimeout,
    DivergentTouches,
    PairSeparationChanged,
    WrongDirection,
    VerticalMotion,
    Timeout,
    InsufficientDistance,
    InsufficientHorizontalDominance,
    PageUnavailable,
    BlockedSource,
    LostCapture,
    Disposed,
}

internal readonly record struct DesktopPageTouchSwipeSample(
    int TouchId,
    double X,
    double Y,
    long TimestampMilliseconds);

internal readonly record struct DesktopPageTouchSurface(double Width, double Height)
{
    internal bool IsValid =>
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;

    internal bool Contains(DesktopPageTouchSwipeSample sample) =>
        sample.X >= 0 && sample.X <= Width && sample.Y >= 0 && sample.Y <= Height;
}

internal readonly record struct DesktopPageTouchSwipeUpdate(
    DesktopPageTouchSwipeUpdateKind Kind,
    DesktopPageTouchSwipeDirection? Direction,
    double Progress,
    double HorizontalTravel,
    DesktopPageTouchSwipeCancelReason CancelReason)
{
    internal static DesktopPageTouchSwipeUpdate None =>
        new(DesktopPageTouchSwipeUpdateKind.None, null, 0, 0, DesktopPageTouchSwipeCancelReason.None);

    internal static DesktopPageTouchSwipeUpdate Cancelled(
        DesktopPageTouchSwipeDirection? direction,
        double horizontalTravel,
        DesktopPageTouchSwipeCancelReason reason) =>
        new(DesktopPageTouchSwipeUpdateKind.Cancelled, direction, 0, horizontalTravel, reason);
}

internal sealed record DesktopPageTouchSwipeOptions
{
    internal bool Enabled { get; init; } = false;

    // v6.8 compatibility path. Fences parity is provided by TwoFingerEnabled.
    internal bool OneFingerEdgeEnabled { get; init; } = true;
    internal bool TwoFingerEnabled { get; init; } = true;

    internal double EdgeWidthDip { get; init; } = 36;
    internal double MinimumCommitDistanceDip { get; init; } = 96;
    internal double MaximumVerticalDriftDip { get; init; } = 72;
    internal double HorizontalDominanceRatio { get; init; } = 1.25;
    internal double WrongDirectionToleranceDip { get; init; } = 16;
    internal int MaximumGestureDurationMilliseconds { get; init; } = 1200;

    // Two-finger gesture tuning.
    internal int TwoFingerJoinWindowMilliseconds { get; init; } = 450;
    internal double DirectionLockDistanceDip { get; init; } = 10;
    internal double MaximumPairSeparationChangeDip { get; init; } = 96;
    internal double ParallelMotionToleranceDip { get; init; } = 32;

    internal DesktopPageTouchSwipeOptions Normalize() => this with
    {
        EdgeWidthDip = ClampFinite(EdgeWidthDip, 12, 160, 36),
        MinimumCommitDistanceDip = ClampFinite(MinimumCommitDistanceDip, 48, 320, 96),
        MaximumVerticalDriftDip = ClampFinite(MaximumVerticalDriftDip, 24, 240, 72),
        HorizontalDominanceRatio = ClampFinite(HorizontalDominanceRatio, 1.05, 3.0, 1.25),
        WrongDirectionToleranceDip = ClampFinite(WrongDirectionToleranceDip, 0, 64, 16),
        MaximumGestureDurationMilliseconds = Math.Clamp(MaximumGestureDurationMilliseconds, 250, 3000),
        TwoFingerJoinWindowMilliseconds = Math.Clamp(TwoFingerJoinWindowMilliseconds, 120, 1000),
        DirectionLockDistanceDip = ClampFinite(DirectionLockDistanceDip, 2, 48, 10),
        MaximumPairSeparationChangeDip = ClampFinite(MaximumPairSeparationChangeDip, 24, 240, 96),
        ParallelMotionToleranceDip = ClampFinite(ParallelMotionToleranceDip, 8, 120, 32),
    };

    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
    {
        if (!double.IsFinite(value))
        {
            return fallback;
        }

        return Math.Clamp(value, minimum, maximum);
    }
}
