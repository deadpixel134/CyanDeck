namespace DesktopDeck.Services;

/// <summary>Side-effect-free decision shared by native desktop visibility probes.</summary>
internal static class DesktopShowDesktopRecoveryPolicy
{
    internal static bool NeedsRecovery(bool expectedVisible, bool wpfVisible,
        bool wpfMinimized, bool nativeVisible, bool nativeMinimized,
        bool shellForeground, bool attachedToDesktopBand)
        => expectedVisible && (!wpfVisible || wpfMinimized || !nativeVisible
            || nativeMinimized || (shellForeground && !attachedToDesktopBand));
}
