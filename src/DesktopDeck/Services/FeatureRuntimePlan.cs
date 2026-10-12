namespace DesktopDeck.Services;

/// <summary>
/// Effective module startup choices. User preferences remain persisted even
/// when Safe Mode temporarily prevents one or both modules from running.
/// The tray/controller is always available, including when both are disabled.
/// </summary>
internal readonly record struct FeatureRuntimePlan(
    bool StartDesktopDeck,
    bool StartCalendar,
    bool IsSafeMode)
{
    public static FeatureRuntimePlan Create(bool enableDesktopDeck, bool enableCalendar, bool safeMode)
        => new(!safeMode && enableDesktopDeck, !safeMode && enableCalendar, safeMode);
}
