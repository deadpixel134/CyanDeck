namespace DesktopDeck.Automation;

public sealed record ContinuousAutomationOptions
{
    // Migration-safe default: preserve DesktopDeck's existing always-updated behavior.
    public bool KeepRulesUpdatedAtAllTimes { get; init; } = true;
}

public enum AutomationUpdateTrigger
{
    ManualApplyRulesNow,
    RuleEditorPreview,
    RuleEditorSaved,
    NewDesktopItem,
    DesktopWatcherReconcile,
    ItemMetadataChanged,
    MaintenanceTick,
    StartupReconcile,
    ImportCompleted,
    RecoveryRestoreCompleted
}

public enum AutomationUpdateDecisionCode
{
    RunRuleMoves,
    PreviewOnly,
    ContinuousUpdatesDisabled,
    MasterDisabled,
    SafeMode,
    CapabilityDenied,
    RestoreBoundary
}

public readonly record struct AutomationUpdateContext(
    bool KeepRulesUpdatedAtAllTimes,
    bool MasterEnabled,
    bool SafeMode,
    bool CapabilityAllowed);

public readonly record struct AutomationUpdateDecision(
    bool EvaluateRules,
    bool AllowMoves,
    AutomationUpdateDecisionCode Code);

public enum ContinuousAutomationSettingResult
{
    Unchanged,
    Saved,
    SaveFailedRolledBack
}
