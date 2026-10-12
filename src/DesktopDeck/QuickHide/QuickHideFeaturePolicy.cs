namespace DesktopDeck.QuickHide;

/// <summary>
/// Pure policy for the Quick-hide enable gate and startup visibility behavior.
/// It does not touch windows, Explorer, persistence, hotkeys, or timers.
/// </summary>
public static class QuickHideFeaturePolicy
{
    public static QuickHideGateDecision EvaluateCommand(
        QuickHideFeatureOptions? options,
        QuickHideCommandIntent intent,
        QuickHideInvocationSource source,
        bool isQuickHideActive)
    {
        options ??= new QuickHideFeatureOptions();

        // Force-show is a safety/recovery operation. It must remain available even
        // when user-triggered Quick-hide is disabled, otherwise a hidden desktop
        // could be stranded after disabling the feature.
        if (intent == QuickHideCommandIntent.ForceShow)
        {
            return isQuickHideActive
                ? QuickHideGateDecision.Allow
                : QuickHideGateDecision.IgnoreAlreadyVisible;
        }

        if (!options.Enabled)
            return QuickHideGateDecision.IgnoreFeatureDisabled;

        return QuickHideGateDecision.Allow;
    }

    public static QuickHideEnabledChangePlan PlanEnabledChange(
        bool currentEnabled,
        bool requestedEnabled,
        bool isQuickHideActive,
        bool hasHiddenVisualResidue)
    {
        if (currentEnabled == requestedEnabled)
            return QuickHideEnabledChangePlan.NoChange;

        if (!requestedEnabled && (isQuickHideActive || hasHiddenVisualResidue))
            return QuickHideEnabledChangePlan.ShowThenPersistDisabled;

        return QuickHideEnabledChangePlan.PersistOnly;
    }

    public static QuickHideShowReason? GetStartupShowReason(
        QuickHideFeatureOptions? options,
        QuickHideRuntimeSnapshot state)
    {
        options ??= new QuickHideFeatureOptions();

        if (!state.IsQuickHideActive && !state.HasHiddenVisualResidue)
            return null;

        // A disabled feature must never leave the desktop logically quick-hidden.
        if (!options.Enabled)
            return QuickHideShowReason.DisabledFeatureStartupRepair;

        if (options.AlwaysShowIconsAtStartup)
            return QuickHideShowReason.StartupAlwaysShow;

        return null;
    }

    public static bool IsUserToggleSource(QuickHideInvocationSource source) => source switch
    {
        QuickHideInvocationSource.Hotkey => true,
        QuickHideInvocationSource.Tray => true,
        QuickHideInvocationSource.DesktopDoubleClick => true,
        _ => false
    };
}
