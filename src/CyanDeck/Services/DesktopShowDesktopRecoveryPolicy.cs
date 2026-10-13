namespace CyanDeck.Services;

/// <summary>Side-effect-free decision shared by native desktop visibility probes.</summary>
internal static class DesktopShowDesktopRecoveryPolicy
{
    internal static bool NeedsRecovery(bool expectedVisible, bool wpfVisible,
        bool wpfMinimized, bool nativeVisible, bool nativeMinimized,
        bool shellForeground, bool attachedToDesktopBand)
        => expectedVisible && (!wpfVisible || wpfMinimized || !nativeVisible
            || nativeMinimized || (shellForeground && !attachedToDesktopBand));

    // A visible HWND can be buried under a rebuilt Explorer desktop even when
    // both WPF and Win32 say "visible". Require multiple consecutive unhealthy
    // probes to avoid reacting to a transient WorkerW / virtual-desktop change.
    // A native minimize/hide has direct evidence and does not need debounce.
    // Persistent incidents may retry at most once per 30 seconds; no per-tick
    // SetWindowPos operations and no unbounded repaint loop.
    internal static bool ShouldSchedulePersistentRepair(
        bool needsRecovery, bool nativeOrWpfHidden, int consecutiveUnhealthyProbes,
        DateTime nowUtc, DateTime lastAttemptUtc, bool repairPending)
        => needsRecovery && !repairPending
            && (nativeOrWpfHidden || consecutiveUnhealthyProbes >= 3)
            && nowUtc - lastAttemptUtc >= TimeSpan.FromSeconds(30);

}
