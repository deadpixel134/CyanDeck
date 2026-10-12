namespace DesktopDeck.Layout;

public static class LinkedIconSpacingIntegrationContract
{
    public const string SettingKey = "Layout.UseSameSpacingInsideAndOutsideGroups";
    public const string ManagedCapability = "Layout.IconSpacing.Edit";

    public static IconSpacing ResolveBaseSpacing(
        bool masterEnabled,
        bool safeMode,
        bool linked,
        ItemSurfaceLayoutKind surfaceKind,
        IconSpacing savedGroupSpacing,
        IconSpacing looseDesktopSpacing)
        => LinkedIconSpacingPolicy.Resolve(new LinkedIconSpacingContext(
            masterEnabled,
            safeMode,
            CanEditLayout: true,
            linked,
            surfaceKind,
            savedGroupSpacing,
            looseDesktopSpacing)).EffectiveSpacing;
}
