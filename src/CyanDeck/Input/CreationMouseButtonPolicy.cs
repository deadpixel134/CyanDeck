namespace CyanDeck.Input;

internal static class CreationMouseButtonPolicy
{
    internal static bool Allows(CreationMouseButtonMode mode, DesktopCreationMouseButton button) =>
        mode switch
        {
            CreationMouseButtonMode.LeftOnly => button == DesktopCreationMouseButton.Left,
            CreationMouseButtonMode.RightOnly => button == DesktopCreationMouseButton.Right,
            CreationMouseButtonMode.Either => button is DesktopCreationMouseButton.Left or DesktopCreationMouseButton.Right,
            _ => false,
        };

    internal static CreationGestureArmDecision CanArm(
        DesktopCreationMouseButton button,
        CreationGestureContext context,
        CreationMouseButtonOptions options)
    {
        if (!Allows(options.Mode, button))
        {
            return CreationGestureArmDecision.Ignore;
        }

        if (!context.HostCanCreateGroups || !context.IsBlankDesktop || !context.IsDesktopInputEligible)
        {
            return CreationGestureArmDecision.Ignore;
        }

        if (context.ConflictingPageGestureActive || context.ModifierGestureActive)
        {
            return CreationGestureArmDecision.Ignore;
        }

        // Exactly one supported mouse button must own the pending gesture. This leaves the
        // existing simultaneous left+right Desktop Pages gesture available to the host.
        bool exactButtonState = button switch
        {
            DesktopCreationMouseButton.Left => context.LeftButtonDown && !context.RightButtonDown,
            DesktopCreationMouseButton.Right => context.RightButtonDown && !context.LeftButtonDown,
            _ => false,
        };

        return exactButtonState
            ? CreationGestureArmDecision.ArmWithoutConsuming
            : CreationGestureArmDecision.Ignore;
    }

    internal static bool CrossedDragThreshold(
        CreationGesturePoint start,
        CreationGesturePoint current,
        CreationMouseButtonOptions options)
    {
        CreationMouseButtonOptions normalized = options.Normalize();
        return Math.Abs(current.X - start.X) >= normalized.HorizontalDragThresholdDip
            || Math.Abs(current.Y - start.Y) >= normalized.VerticalDragThresholdDip;
    }
}
