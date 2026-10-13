namespace CyanDeck.Appearance;

/// <summary>
/// Names used by Settings/managed-policy integration. This file intentionally contains no reflection-based
/// configuration writer; it is a typed contract for the existing CyanDeck settings adapter.
/// </summary>
internal static class IconMovementAnimationIntegrationContract
{
    internal const string SettingEnabled = "Appearance.AnimateIconsWhenMoved";
    internal const string SettingDurationMs = "Appearance.IconMovementAnimationDurationMs";
    internal const string SettingMaxItems = "Performance.IconMovementAnimationMaxItems";
    internal const string ManagedCapability = "Appearance.Edit";
}
