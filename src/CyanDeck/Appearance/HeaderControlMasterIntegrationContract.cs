namespace CyanDeck.Appearance;

/// <summary>
/// Integration invariants for v7.25. The full source owns the actual titlebar/tab controls.
/// </summary>
internal static class HeaderControlMasterIntegrationContract
{
    internal const string ManagedCapability = "Appearance.Edit";
    internal const string SettingKey = "Appearance.ShowControlsInGroupHeaders";

    internal const string DefaultRule =
        "Migration default is true so existing CyanDeck titlebar controls remain unchanged after upgrade.";

    internal const string CompositionRule =
        "When enabled, clear the master suppression and defer to v1.9 selected-tab mouseover policy and v7.8 inactive-tab policy; never force controls visible.";

    internal const string ScopeRule =
        "Suppress only header button/control presenters. Do not hide the title/header strip, tab labels, Folder Portal folder icon, drag strip, or right-click header menu.";

    internal const string AccessibilityRule =
        "Suppressed controls must be Collapsed or otherwise removed from hit testing and keyboard tab navigation; restoring the option returns ownership to existing bindings/styles.";
}
