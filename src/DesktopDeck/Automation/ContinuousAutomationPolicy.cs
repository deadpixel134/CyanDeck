namespace DesktopDeck.Automation;

public static class ContinuousAutomationPolicy
{
    public static AutomationUpdateDecision Decide(
        AutomationUpdateTrigger trigger,
        in AutomationUpdateContext context)
    {
        // Preview is deliberately non-mutating and remains available even when
        // continuous maintenance is disabled. This keeps the rule editor useful
        // before the user explicitly chooses Apply rules now.
        if (trigger == AutomationUpdateTrigger.RuleEditorPreview)
            return new(true, false, AutomationUpdateDecisionCode.PreviewOnly);

        if (!context.MasterEnabled)
            return No(AutomationUpdateDecisionCode.MasterDisabled);
        if (context.SafeMode)
            return No(AutomationUpdateDecisionCode.SafeMode);
        if (!context.CapabilityAllowed)
            return No(AutomationUpdateDecisionCode.CapabilityDenied);

        // Import/recovery restore is a layout/state boundary. Never immediately
        // re-sort the just-restored state. The next ordinary automatic trigger
        // may update it if continuous maintenance is enabled.
        if (trigger is AutomationUpdateTrigger.ImportCompleted or
            AutomationUpdateTrigger.RecoveryRestoreCompleted)
        {
            return No(AutomationUpdateDecisionCode.RestoreBoundary);
        }

        // Manual Apply is always an explicit one-shot operation. It does not
        // turn continuous maintenance back on.
        if (trigger == AutomationUpdateTrigger.ManualApplyRulesNow)
            return new(true, true, AutomationUpdateDecisionCode.RunRuleMoves);

        if (!context.KeepRulesUpdatedAtAllTimes)
            return No(AutomationUpdateDecisionCode.ContinuousUpdatesDisabled);

        return new(true, true, AutomationUpdateDecisionCode.RunRuleMoves);
    }

    public static bool ShouldRunDefaultDestinationFallback(
        AutomationUpdateTrigger trigger,
        in AutomationUpdateDecision decision,
        bool anyRuleMatched)
    {
        if (trigger != AutomationUpdateTrigger.NewDesktopItem)
            return false;

        // v7.13 default-destination behavior is independent of continuous rule
        // maintenance. If rule evaluation is disabled, there is no rule match to
        // honor, so the new item may still use the configured default destination.
        if (!decision.EvaluateRules)
            return true;

        return !anyRuleMatched;
    }

    private static AutomationUpdateDecision No(AutomationUpdateDecisionCode code) =>
        new(false, false, code);
}
