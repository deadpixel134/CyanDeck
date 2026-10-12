using System;

namespace DesktopDeck.Animations;

/// <summary>
/// Pure transition planner for Desktop Pages. All distances are WPF device-independent pixels.
/// It performs no I/O, timers, screen enumeration, persistence, or animation scheduling.
/// </summary>
internal static class DesktopPageTransitionPlanner
{
    internal static DesktopPageTransitionPlan Create(
        DesktopPageTransitionRequest request,
        DesktopPageTransitionOptions options)
    {
        options = options.Normalize();

        if (!options.AnimationsEnabled || !request.SystemAnimationsAllowed)
        {
            return DesktopPageTransitionPlan.Immediate;
        }

        if (!double.IsFinite(request.SurfaceWidthDip) || request.SurfaceWidthDip <= 0)
        {
            return DesktopPageTransitionPlan.Immediate;
        }

        double width = Math.Clamp(request.SurfaceWidthDip, 160, 16384);
        double progress = NormalizeProgress(request.StartingProgress);
        double sign = request.Direction == DesktopPageTransitionDirection.Next ? 1.0 : -1.0;

        if (request.Phase == DesktopPageTransitionPhase.Cancel)
        {
            double currentOffset = -sign * width * progress;
            int cancelDuration = ScaleDuration(options, progress);
            return new DesktopPageTransitionPlan(
                Animate: progress > 0,
                DurationMilliseconds: progress > 0 ? cancelDuration : 0,
                OutgoingFromX: currentOffset,
                OutgoingToX: 0,
                IncomingFromX: 0,
                IncomingToX: 0,
                IncomingFromOpacity: 1,
                IncomingToOpacity: 1,
                EasingProfile: ResolveProfile(options),
                EnhancedEasing: options.EnhancedEasingEnabled,
                UseLightBounce: false);
        }

        double outgoingFrom = -sign * width * progress;
        double outgoingTo = -sign * width;
        double incomingFrom = sign * width * (1.0 - progress);
        double incomingTo = 0;
        double incomingOpacity = Lerp(options.IncomingFadeStartOpacity, 1.0, progress);
        double remaining = 1.0 - progress;
        int duration = ScaleDuration(options, remaining);

        if (remaining <= 0)
        {
            return DesktopPageTransitionPlan.Immediate;
        }

        return new DesktopPageTransitionPlan(
            Animate: true,
            DurationMilliseconds: duration,
            OutgoingFromX: outgoingFrom,
            OutgoingToX: outgoingTo,
            IncomingFromX: incomingFrom,
            IncomingToX: incomingTo,
            IncomingFromOpacity: incomingOpacity,
            IncomingToOpacity: 1,
            EasingProfile: ResolveProfile(options),
            EnhancedEasing: options.EnhancedEasingEnabled,
            UseLightBounce: options.LightBounceEnabled && options.EnhancedEasingEnabled);
    }

    private static DesktopPageEasingProfile ResolveProfile(DesktopPageTransitionOptions options) =>
        options.EnhancedEasingEnabled ? options.EasingProfile : DesktopPageEasingProfile.Legacy;

    private static int ScaleDuration(DesktopPageTransitionOptions options, double remainingFraction)
    {
        remainingFraction = Math.Clamp(remainingFraction, 0, 1);
        if (remainingFraction <= 0)
        {
            return 0;
        }

        int scaled = (int)Math.Round(options.DurationMilliseconds * remainingFraction, MidpointRounding.AwayFromZero);
        return Math.Clamp(scaled, options.MinimumContinuationMilliseconds, options.DurationMilliseconds);
    }

    private static double NormalizeProgress(double progress)
    {
        if (!double.IsFinite(progress))
        {
            return 0;
        }

        return Math.Clamp(progress, 0, 1);
    }

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);
}
