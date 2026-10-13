using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CyanDeck.Appearance;

internal readonly record struct WpfIconMovementAnimationTarget(
    FrameworkElement Element,
    TranslateTransform MovementTransform);

/// <summary>
/// Applies planner output to a host-provided, dedicated TranslateTransform channel.
/// The host must not reuse this transform for drag feedback, tint, scaling, or other animations.
/// </summary>
internal sealed class WpfIconMovementAnimator
{
    private readonly Func<string, string, WpfIconMovementAnimationTarget?> _resolveTarget;

    internal WpfIconMovementAnimator(
        Func<string, string, WpfIconMovementAnimationTarget?> resolveTarget)
    {
        _resolveTarget = resolveTarget ?? throw new ArgumentNullException(nameof(resolveTarget));
    }

    internal void Apply(IconMovementAnimationPlan plan)
    {
        if (!plan.ShouldAnimate)
        {
            return;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(plan.DurationMs);
        CubicEase easing = new() { EasingMode = EasingMode.EaseOut };
        if (easing.CanFreeze)
        {
            easing.Freeze();
        }

        foreach (IconMovementVector move in plan.Moves)
        {
            WpfIconMovementAnimationTarget? resolved = _resolveTarget(move.SurfaceId, move.ItemId);
            if (resolved is not { } target ||
                !target.Element.IsLoaded ||
                target.Element.Visibility != Visibility.Visible ||
                target.MovementTransform.IsFrozen)
            {
                continue;
            }

            AnimateAxis(
                target.MovementTransform,
                TranslateTransform.XProperty,
                move.FromOffsetXDip,
                duration,
                easing);
            AnimateAxis(
                target.MovementTransform,
                TranslateTransform.YProperty,
                move.FromOffsetYDip,
                duration,
                easing);
        }
    }

    internal static bool ReadSystemAnimationPreference() => SystemParameters.ClientAreaAnimation;

    internal static void Cancel(WpfIconMovementAnimationTarget target)
    {
        if (target.MovementTransform.IsFrozen)
        {
            return;
        }

        target.MovementTransform.BeginAnimation(TranslateTransform.XProperty, null);
        target.MovementTransform.BeginAnimation(TranslateTransform.YProperty, null);
        target.MovementTransform.X = 0.0;
        target.MovementTransform.Y = 0.0;
    }

    private static void AnimateAxis(
        TranslateTransform transform,
        DependencyProperty property,
        double from,
        TimeSpan duration,
        IEasingFunction easing)
    {
        transform.BeginAnimation(property, null);

        // Base value is final layout position (zero render offset). FillBehavior.Stop safely lands there.
        transform.SetValue(property, 0.0);
        DoubleAnimation animation = new()
        {
            From = from,
            To = 0.0,
            Duration = new Duration(duration),
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        };
        if (animation.CanFreeze)
        {
            animation.Freeze();
        }

        transform.BeginAnimation(
            property,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }
}
