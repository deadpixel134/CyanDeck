namespace CyanDeck.Services;

// Testable read-only recovery decision: only undo an unexpected Explorer
// FolderView show after a stable display transition. Missing shell views,
// safe mode and deliberate native-icon visibility never trigger a hide.
internal static class NativeDesktopIconRecoveryPolicy
{
    internal static bool ShouldRehide(
        bool desktopRuntimeStarted,
        bool safeMode,
        bool shouldKeepHidden,
        bool displayStable,
        bool? explorerListViewVisible) =>
        desktopRuntimeStarted && !safeMode && shouldKeepHidden &&
        displayStable && explorerListViewVisible == true;
}
