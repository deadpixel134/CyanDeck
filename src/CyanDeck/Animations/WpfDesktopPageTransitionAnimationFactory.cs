using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace CyanDeck.Animations;

/// <summary>
/// Creates WPF animations from a framework-neutral transition plan. The caller owns
/// Storyboard/BeginAnimation lifecycle and remains responsible for committing page state.
/// </summary>
internal static class WpfDesktopPageTransitionAnimationFactory
{
    internal static bool AreSystemAnimationsAllowed() => SystemParameters.ClientAreaAnimation;

    internal static DoubleAnimation CreateOutgoingSlide(DesktopPageTransitionPlan plan) =>
        CreateDouble(plan.OutgoingFromX, plan.OutgoingToX, plan);

    internal static DoubleAnimation CreateIncomingSlide(DesktopPageTransitionPlan plan) =>
        CreateDouble(plan.IncomingFromX, plan.IncomingToX, plan);

    internal static DoubleAnimation CreateIncomingOpacity(DesktopPageTransitionPlan plan) =>
        CreateDouble(plan.IncomingFromOpacity, plan.IncomingToOpacity, plan, allowBounce: false);

    private static DoubleAnimation CreateDouble(
        double from,
        double to,
        DesktopPageTransitionPlan plan,
        bool allowBounce = true)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, plan.DurationMilliseconds)),
            FillBehavior = FillBehavior.Stop,
        };

        if (plan.Animate && plan.EnhancedEasing)
        {
            animation.EasingFunction = CreateEasing(plan, allowBounce);
        }

        return animation;
    }

    private static IEasingFunction? CreateEasing(DesktopPageTransitionPlan plan, bool allowBounce)
    {
        if (allowBounce && plan.UseLightBounce)
        {
            return new BackEase
            {
                Amplitude = 0.16,
                EasingMode = EasingMode.EaseOut,
            };
        }

        return plan.EasingProfile switch
        {
            DesktopPageEasingProfile.Smooth => new CubicEase { EasingMode = EasingMode.EaseOut },
            DesktopPageEasingProfile.Snappy => new QuadraticEase { EasingMode = EasingMode.EaseOut },
            DesktopPageEasingProfile.Gentle => new SineEase { EasingMode = EasingMode.EaseInOut },
            _ => null,
        };
    }
}
