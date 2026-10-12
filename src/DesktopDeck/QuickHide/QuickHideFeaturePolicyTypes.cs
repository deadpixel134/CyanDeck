using System;

namespace DesktopDeck.QuickHide;

public enum QuickHideInvocationSource
{
    Hotkey,
    Tray,
    DesktopDoubleClick,
    PageSwitchAutoUnhide,
    StartupNormalization,
    RecoveryRepair,
    Programmatic
}

public enum QuickHideCommandIntent
{
    Toggle,
    ForceShow
}

public enum QuickHideGateDecision
{
    Allow,
    IgnoreFeatureDisabled,
    IgnoreAlreadyVisible
}

public enum QuickHideShowReason
{
    UserDisabledFeature,
    StartupAlwaysShow,
    DisabledFeatureStartupRepair,
    PageSwitchAutoUnhide,
    RecoveryRepair
}

public enum QuickHideEnabledChangePlan
{
    NoChange,
    PersistOnly,
    ShowThenPersistDisabled
}

public enum QuickHideEnabledChangeStatus
{
    NoChange,
    ReadyToPersist,
    ShowFailedDoNotPersist
}

public enum QuickHideStartupApplyStatus
{
    Applied,
    NoActionNeeded,
    DeferredDesktopNotReady,
    DeferredDisplayTransition,
    SkippedSafeMode,
    SkippedMasterDisabled,
    FailedRetryable,
    AlreadyCompleted
}

public sealed record QuickHideFeatureOptions
{
    // Preserve the historical DesktopDeck behavior on migration.
    public bool Enabled { get; init; } = true;

    // Preserve the historical startup behavior on migration.
    public bool AlwaysShowIconsAtStartup { get; init; } = false;
}

public readonly record struct QuickHideRuntimeSnapshot(
    bool IsQuickHideActive,
    bool HasHiddenVisualResidue,
    bool DesktopShellReady,
    bool DisplayTransitionActive,
    bool SafeMode,
    bool MasterDisabled);

public readonly record struct QuickHideEnabledChangeResult(
    QuickHideEnabledChangeStatus Status,
    bool ValueToPersist,
    bool VisibilityWasNormalized);

public readonly record struct QuickHideStartupApplyResult(
    QuickHideStartupApplyStatus Status,
    bool VisibilityWasNormalized,
    QuickHideShowReason? ShowReason);
