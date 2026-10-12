namespace DesktopDeck.Input;

internal static class CreationMouseButtonIntegrationContract
{
    internal const string SettingKey = "General.CreateGroupMouseButtonMode";
    internal const string ManagedCapability = "Layout.CreateDeleteGroups";

    internal static CreationMouseButtonMode SeedFromLegacyHost(bool legacyLeftEnabled, bool legacyRightEnabled)
    {
        return (legacyLeftEnabled, legacyRightEnabled) switch
        {
            (true, false) => CreationMouseButtonMode.LeftOnly,
            (false, true) => CreationMouseButtonMode.RightOnly,
            (true, true) => CreationMouseButtonMode.Either,
            _ => throw new InvalidOperationException(
                "Legacy DesktopDeck creation input must expose at least one mouse button before v7.23 migration."),
        };
    }

    internal const string PromotionRule =
        "Arm without consuming; consume only after the configured button crosses the existing drag threshold; " +
        "release before promotion returns to the host, preserving right-click context menus and left-click selection.";

    internal const string MultiButtonRule =
        "If both left and right buttons become down, cancel creation ownership so the existing Desktop Pages chord can run.";
}
