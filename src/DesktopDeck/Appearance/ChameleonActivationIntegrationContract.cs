namespace DesktopDeck.Appearance;

/// <summary>
/// Integration names/invariants for applying v7.22 to the full DesktopDeck source tree.
/// This overlay does not replace DesktopDeck's existing Chameleon color sampling/blend engine.
/// </summary>
internal static class ChameleonActivationIntegrationContract
{
    internal const string ManagedCapability = "Appearance.Edit";
    internal const string HoverSettingKey = "Appearance.ChameleonFullColorOnMouseover";
    internal const string DesktopUseSettingKey = "Appearance.ChameleonFullColorWhenDesktopInUse";

    internal const string SharedSourceRule =
        "Create exactly one WindowsForegroundEventSource for the process; group bridges subscribe to it and never own it.";

    internal const string ExistingFocusClassifierRule =
        "Reuse the v1.8 desktop-or-DesktopDeck foreground classifier; do not add Shell_TrayWnd/Progman/WorkerW discovery here.";

    internal const string PresentationRule =
        "ShowFullColor temporarily bypasses Chameleon blending only; it must not mutate saved group colors, opacity, icon tint, or wallpaper samples.";
}
