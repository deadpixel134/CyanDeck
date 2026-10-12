namespace DesktopDeck.QuickHide;

/// <summary>
/// Small guard helpers intended for the host integration points.
/// </summary>
public static class LooseIconQuickHideIntegrationGuard
{
    public const string ManagedCapabilityName = "QuickHide.IconExclusion.Edit";

    public static bool IsPerItemExclusionEffective(
        bool quickHideFeatureEnabled,
        bool quickHideActive,
        QuickHideVisibilityMode mode,
        QuickHideLooseIconSurfaceKind surfaceKind)
    {
        if (!quickHideFeatureEnabled || !quickHideActive)
            return false;

        if (mode == QuickHideVisibilityMode.HideGroupsOnly)
            return false;

        return surfaceKind == QuickHideLooseIconSurfaceKind.DesktopDeckManagedLooseIcon;
    }

    public static bool MayOfferEditCommand(LooseDesktopItemQuickHideSnapshot? item, bool capabilityAllowed)
    {
        return capabilityAllowed &&
               item is not null &&
               item.IsEditableManagedLooseIcon;
    }
}
