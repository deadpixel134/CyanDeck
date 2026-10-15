namespace CyanDeck.Services;

// Immutable, side-effect-free action policy for the tray's unified visibility
// repair. Recovery never removes user files, changes Google state or overrides
// a live monitor transition. Safe mode remains observation-only.
internal readonly record struct DisplaySurfaceRecoveryPlan(
    bool RepairGroups,
    bool InspectCalendar,
    bool BlockedByDisplayTransition)
{
    internal bool HasWork => RepairGroups || InspectCalendar;

    internal static DisplaySurfaceRecoveryPlan Create(
        bool safeMode,
        bool deckEnabled,
        bool desktopRuntimeReady,
        bool calendarEnabled,
        bool displayStable)
    {
        if (safeMode)
            return default;
        if (!displayStable)
            return new DisplaySurfaceRecoveryPlan(false, false, true);
        return new DisplaySurfaceRecoveryPlan(
            deckEnabled && desktopRuntimeReady,
            calendarEnabled,
            false);
    }
}
