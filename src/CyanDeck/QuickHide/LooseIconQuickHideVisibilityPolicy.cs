using System;

namespace CyanDeck.QuickHide;

/// <summary>
/// Pure visibility policy for per-item Quick-hide exclusions.
/// This layer never touches Explorer's SysListView32, windows, files, registry, or input hooks.
/// </summary>
public static class LooseIconQuickHideVisibilityPolicy
{
    public static LooseIconQuickHideVisibilityDecision Evaluate(LooseIconQuickHideVisibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Item);

        var item = request.Item;

        if (item.SurfaceKind == QuickHideLooseIconSurfaceKind.NativeExplorerDesktopIcon)
            return LooseIconQuickHideVisibilityDecision.DeferToNativeDesktopLayer;

        if (item.SurfaceKind == QuickHideLooseIconSurfaceKind.GroupItem || item.IsGroupMember)
            return LooseIconQuickHideVisibilityDecision.DeferToGroupPolicy;

        if (item.SurfaceKind != QuickHideLooseIconSurfaceKind.CyanDeckManagedLooseIcon ||
            !item.IsLooseDesktopItem)
        {
            return LooseIconQuickHideVisibilityDecision.IgnoreUnsupportedSurface;
        }

        if (!request.QuickHideFeatureEnabled || !request.IsQuickHideActive)
            return LooseIconQuickHideVisibilityDecision.PreserveNormalVisibility;

        // Hide-groups-only never hides loose desktop icons, so the exclusion flag is dormant.
        if (request.Mode == QuickHideVisibilityMode.HideGroupsOnly)
            return LooseIconQuickHideVisibilityDecision.PreserveNormalVisibility;

        return item.ExcludeFromQuickHide
            ? LooseIconQuickHideVisibilityDecision.KeepVisibleExcluded
            : LooseIconQuickHideVisibilityDecision.HideLooseIcon;
    }

    public static bool ShouldRenderOnManagedLooseIconSurface(LooseIconQuickHideVisibilityRequest request)
    {
        return Evaluate(request) switch
        {
            LooseIconQuickHideVisibilityDecision.HideLooseIcon => false,
            LooseIconQuickHideVisibilityDecision.IgnoreUnsupportedSurface => false,
            _ => true
        };
    }
}
