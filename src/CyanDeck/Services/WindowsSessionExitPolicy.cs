namespace CyanDeck.Services;

/// <summary>
/// Windows logoff/shutdown must not run disk-heavy forced checkpoints or
/// Explorer/shell cleanup on the UI thread. Existing debounced autosaves
/// remain the source of truth. If state was not safely persisted, preserve
/// the session recovery marker for the next startup instead of lying that
/// everything exited cleanly.
/// </summary>
public static class WindowsSessionExitPolicy
{
    public static bool CanClearRecoveryMarker(
        bool abnormalTermination,
        bool hasUnsavedLayoutChanges,
        int pendingStorageRetries,
        bool displayTransitionInProgress,
        bool layoutOperationInProgress)
        => !abnormalTermination
            && !hasUnsavedLayoutChanges
            && pendingStorageRetries == 0
            && !displayTransitionInProgress
            && !layoutOperationInProgress;
}
