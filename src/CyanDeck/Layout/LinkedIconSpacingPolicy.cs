namespace CyanDeck.Layout;

public static class LinkedIconSpacingPolicy
{
    public static LinkedIconSpacingDecision Resolve(LinkedIconSpacingContext context)
    {
        var saved = IconSpacing.Normalize(context.SavedGroupSpacing, new IconSpacing(0, 0));
        var desktop = IconSpacing.Normalize(context.DesktopLooseIconSpacing, saved);

        var linked = context.MasterEnabled
            && !context.IsSafeMode
            && context.UseSameSpacingInsideAndOutsideGroups;

        if (!linked)
        {
            return new LinkedIconSpacingDecision(
                saved,
                UsesDesktopSpacing: false,
                GroupSpacingControlsEnabled: context.MasterEnabled && !context.IsSafeMode && context.CanEditLayout,
                ShouldRelayoutNow: false);
        }

        var participates = context.SurfaceKind is ItemSurfaceLayoutKind.Grid or ItemSurfaceLayoutKind.FreePlacement;
        if (!participates)
        {
            return new LinkedIconSpacingDecision(
                saved,
                UsesDesktopSpacing: false,
                GroupSpacingControlsEnabled: false,
                ShouldRelayoutNow: false);
        }

        return new LinkedIconSpacingDecision(
            desktop,
            UsesDesktopSpacing: true,
            GroupSpacingControlsEnabled: false,
            ShouldRelayoutNow: context.SurfaceKind == ItemSurfaceLayoutKind.Grid);
    }
}
