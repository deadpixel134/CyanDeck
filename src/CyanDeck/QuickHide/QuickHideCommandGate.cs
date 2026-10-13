namespace CyanDeck.QuickHide;

/// <summary>
/// Small adapter intended to sit in front of the existing centralized Quick-hide command.
/// It never implements hiding/showing itself.
/// </summary>
public static class QuickHideCommandGate
{
    public static bool ShouldInvokeExistingCommand(
        QuickHideFeatureOptions? options,
        QuickHideCommandIntent intent,
        QuickHideInvocationSource source,
        bool isQuickHideActive,
        out QuickHideGateDecision decision)
    {
        decision = QuickHideFeaturePolicy.EvaluateCommand(
            options,
            intent,
            source,
            isQuickHideActive);

        return decision == QuickHideGateDecision.Allow;
    }
}
