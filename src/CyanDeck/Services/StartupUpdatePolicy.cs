namespace CyanDeck.Services;

/// <summary>
/// No desktop/shell HWND should be shown before an automatic start-time update.
/// Manual commands, safe mode, recovery and read-only schema compatibility bypass
/// preflight and retain their original immediate startup behavior.
/// </summary>
public static class StartupUpdatePolicy
{
    public static bool ShouldPreflight(bool autoCheckEnabled, bool autoInstallEnabled,
        bool safeMode, bool readOnly, bool pendingRecovery, bool externalCommand)
        => autoCheckEnabled && autoInstallEnabled && !safeMode && !readOnly
            && !pendingRecovery && !externalCommand;
}
