namespace CyanDeck.Input;

/// <summary>
/// Stateful promotion gate for CyanDeck's existing blank-desktop rectangle creation gesture.
/// A press is only armed; ownership is not consumed until the normal drag threshold is crossed.
/// </summary>
internal sealed class DesktopCreationGestureGate
{
    private DesktopCreationMouseButton _button = DesktopCreationMouseButton.Other;
    private CreationGesturePoint _start;
    private bool _active;
    private bool _promoted;

    internal bool IsActive => _active;
    internal bool IsPromoted => _promoted;
    internal DesktopCreationMouseButton ArmedButton => _button;

    internal CreationGestureArmDecision Begin(
        DesktopCreationMouseButton button,
        CreationGesturePoint start,
        CreationGestureContext context,
        CreationMouseButtonOptions options)
    {
        Reset();

        CreationGestureArmDecision decision = CreationMouseButtonPolicy.CanArm(button, context, options);
        if (decision != CreationGestureArmDecision.ArmWithoutConsuming)
        {
            return decision;
        }

        _button = button;
        _start = start;
        _active = true;
        return decision;
    }

    internal CreationGestureMoveDecision Move(
        CreationGesturePoint current,
        bool leftButtonDown,
        bool rightButtonDown,
        CreationMouseButtonOptions options)
    {
        if (!_active)
        {
            return CreationGestureMoveDecision.NoActiveGesture;
        }

        // A second primary button means another desktop gesture may be starting. Give the
        // whole sequence back to the existing host instead of partially owning it.
        if (leftButtonDown && rightButtonDown)
        {
            Reset();
            return CreationGestureMoveDecision.CancelToHost;
        }

        bool armedButtonStillDown = _button switch
        {
            DesktopCreationMouseButton.Left => leftButtonDown,
            DesktopCreationMouseButton.Right => rightButtonDown,
            _ => false,
        };

        if (!armedButtonStillDown || !CreationMouseButtonPolicy.Allows(options.Mode, _button))
        {
            Reset();
            return CreationGestureMoveDecision.CancelToHost;
        }

        if (_promoted)
        {
            return CreationGestureMoveDecision.ContinueCreationConsumed;
        }

        if (!CreationMouseButtonPolicy.CrossedDragThreshold(_start, current, options))
        {
            return CreationGestureMoveDecision.KeepPendingUnconsumed;
        }

        _promoted = true;
        return CreationGestureMoveDecision.PromoteAndConsume;
    }

    internal CreationGestureReleaseDecision Release(DesktopCreationMouseButton button)
    {
        if (!_active || button != _button)
        {
            return CreationGestureReleaseDecision.NoActiveGesture;
        }

        CreationGestureReleaseDecision decision = _promoted
            ? CreationGestureReleaseDecision.CompleteAndConsume
            : CreationGestureReleaseDecision.ReleaseToHost;

        Reset();
        return decision;
    }

    internal CreationGestureReleaseDecision CancelToHost()
    {
        if (!_active)
        {
            return CreationGestureReleaseDecision.NoActiveGesture;
        }

        Reset();
        return CreationGestureReleaseDecision.CancelToHost;
    }

    private void Reset()
    {
        _button = DesktopCreationMouseButton.Other;
        _start = default;
        _active = false;
        _promoted = false;
    }
}
