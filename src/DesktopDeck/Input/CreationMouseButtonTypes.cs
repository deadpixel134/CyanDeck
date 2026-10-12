namespace DesktopDeck.Input;

internal enum DesktopCreationMouseButton
{
    Left,
    Right,
    Other,
}

internal enum CreationMouseButtonMode
{
    LeftOnly,
    RightOnly,
    Either,
}

internal enum CreationGestureArmDecision
{
    Ignore,
    ArmWithoutConsuming,
}

internal enum CreationGestureMoveDecision
{
    NoActiveGesture,
    KeepPendingUnconsumed,
    PromoteAndConsume,
    ContinueCreationConsumed,
    CancelToHost,
}

internal enum CreationGestureReleaseDecision
{
    NoActiveGesture,
    ReleaseToHost,
    CompleteAndConsume,
    CancelToHost,
}

internal readonly record struct CreationGesturePoint(double X, double Y);

internal readonly record struct DesktopCreationRectangleGesture(
    int StartScreenX,
    int StartScreenY,
    int EndScreenX,
    int EndScreenY,
    DesktopCreationMouseButton Button);

internal readonly record struct CreationGestureContext(
    bool HostCanCreateGroups,
    bool IsBlankDesktop,
    bool IsDesktopInputEligible,
    bool LeftButtonDown,
    bool RightButtonDown,
    bool ConflictingPageGestureActive,
    bool ModifierGestureActive);

internal readonly record struct CreationMouseButtonOptions(
    CreationMouseButtonMode Mode,
    double HorizontalDragThresholdDip,
    double VerticalDragThresholdDip)
{
    internal CreationMouseButtonOptions Normalize() => new(
        Mode,
        Math.Clamp(HorizontalDragThresholdDip, 1.0, 64.0),
        Math.Clamp(VerticalDragThresholdDip, 1.0, 64.0));
}
