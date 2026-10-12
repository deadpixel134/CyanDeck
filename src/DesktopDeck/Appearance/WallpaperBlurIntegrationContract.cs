namespace DesktopDeck.Appearance;

internal static class WallpaperBlurIntegrationContract
{
    internal const string SettingKey = "Appearance.BlurWallpaperBehindGroups";
    internal const string ManagedCapability = "Appearance.Edit";
    internal const bool MigrationDefault = WallpaperBlurPolicy.DefaultEnabled;

    internal static readonly string[] RefreshEvents =
    {
        "GroupLoaded",
        "GroupMoved",
        "GroupResizeCompleted",
        "GroupDpiChanged",
        "MonitorTopologyStabilized",
        "WallpaperChanged",
        "DesktopPageChanged",
        "PeekEntered",
        "PeekExited",
        "AppearanceChanged",
        "ChameleonPresentationChanged",
    };

    internal static readonly string[] RequiredLayerOrder =
    {
        "WallpaperBlurBackdrop",
        "ExistingGroupBackgroundTint",
        "ItemsAndPortalSurface",
        "TabsTitleAndHeaderControls",
    };

    internal const string IntegrationNotes =
        "Apply v7.30 wallpaper snapshot/mapping support first. Add a dedicated Canvas below the existing group background tint and all content. " +
        "Bind the global/per-appearance checkbox to SettingKey with default false. Refresh from actual transient physical group bounds during Peek and page movement, " +
        "but never persist those transient bounds. WallpaperChanged must invalidate the bounded bitmap cache before refresh. " +
        "Do not use composed-screen capture, desktop screenshot reuse, Registry wallpaper keys, polling, or global hooks. " +
        "High Contrast should suppress the blur layer and preserve the normal host background. The fixed blur radius is an independent DesktopDeck value because the public Fences UI does not document an exact radius.";
}
