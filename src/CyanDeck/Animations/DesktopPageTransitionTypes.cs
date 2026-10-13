using System;

namespace CyanDeck.Animations;

internal enum DesktopPageTransitionDirection
{
    Previous = -1,
    Next = 1,
}

public enum DesktopPageEasingProfile
{
    Legacy = 0,
    Smooth,
    Snappy,
    Gentle,
}

internal enum DesktopPageTransitionPhase
{
    Commit = 0,
    Cancel,
}

internal sealed record DesktopPageTransitionOptions
{
    internal bool AnimationsEnabled { get; init; } = true;
    internal bool EnhancedEasingEnabled { get; init; } = false;
    internal DesktopPageEasingProfile EasingProfile { get; init; } = DesktopPageEasingProfile.Smooth;
    internal int DurationMilliseconds { get; init; } = 220;
    internal int MinimumContinuationMilliseconds { get; init; } = 70;
    internal double IncomingFadeStartOpacity { get; init; } = 0.72;
    internal bool LightBounceEnabled { get; init; } = false;

    internal DesktopPageTransitionOptions Normalize() => this with
    {
        DurationMilliseconds = Math.Clamp(DurationMilliseconds, 80, 600),
        MinimumContinuationMilliseconds = Math.Clamp(MinimumContinuationMilliseconds, 40, 180),
        IncomingFadeStartOpacity = ClampFinite(IncomingFadeStartOpacity, 0.35, 1.0, 0.72),
        EasingProfile = Enum.IsDefined(EasingProfile) ? EasingProfile : DesktopPageEasingProfile.Smooth,
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

internal readonly record struct DesktopPageTransitionRequest(
    DesktopPageTransitionDirection Direction,
    DesktopPageTransitionPhase Phase,
    double SurfaceWidthDip,
    double StartingProgress,
    bool SystemAnimationsAllowed);

internal readonly record struct DesktopPageTransitionPlan(
    bool Animate,
    int DurationMilliseconds,
    double OutgoingFromX,
    double OutgoingToX,
    double IncomingFromX,
    double IncomingToX,
    double IncomingFromOpacity,
    double IncomingToOpacity,
    DesktopPageEasingProfile EasingProfile,
    bool EnhancedEasing,
    bool UseLightBounce)
{
    internal static DesktopPageTransitionPlan Immediate => new(
        Animate: false,
        DurationMilliseconds: 0,
        OutgoingFromX: 0,
        OutgoingToX: 0,
        IncomingFromX: 0,
        IncomingToX: 0,
        IncomingFromOpacity: 1,
        IncomingToOpacity: 1,
        EasingProfile: DesktopPageEasingProfile.Legacy,
        EnhancedEasing: false,
        UseLightBounce: false);
}
