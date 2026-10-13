namespace CyanDeck.Appearance;

/// <summary>
/// Integration names and invariants for applying v7.20 to the full CyanDeck source tree.
/// This file intentionally owns no persistence or UI command routing.
/// </summary>
internal static class HiddenHeaderHoverIntegrationContract
{
    internal const string ManagedCapability = "Appearance.Edit";
    internal const string OptionalSettingKey = "Appearance.RevealHiddenHeadersForPortalsAndTabGroups";

    internal const string RuleSummary =
        "Only Hidden titlebars are overridden; Folder Portal or multi-tab groups reveal on hover/focus; " +
        "Always, Mouseover, Roll-up, and ordinary single-Collection Hidden behavior remain owned by existing code.";
}
