namespace DesktopDeck.Appearance;

internal static class HiddenHeaderHoverPolicy
{
    internal static HiddenHeaderHoverDecision Decide(
        HiddenHeaderHoverContext context,
        HiddenHeaderHoverOptions options)
    {
        // Never replace the existing Always/Mouseover engines or Roll-up's forced header behavior.
        if (context.TitleBarVisibility != DesktopDeckTitleBarVisibility.Hidden || context.IsRolledUp)
        {
            return HiddenHeaderHoverDecision.DeferToExistingPolicy;
        }

        if (!context.IsHeaderPresented || !context.IsGroupInteractive)
        {
            return HiddenHeaderHoverDecision.HiddenStrip;
        }

        if (!options.RevealHiddenHeadersForFolderPortalsAndTabGroups)
        {
            return HiddenHeaderHoverDecision.HiddenStrip;
        }

        bool eligibleGroup = context.IsFolderPortalGroup || context.TabCount >= 2;
        if (!eligibleGroup)
        {
            return HiddenHeaderHoverDecision.HiddenStrip;
        }

        if (context.IsPointerOverGroup)
        {
            return HiddenHeaderHoverDecision.RevealFullHeader;
        }

        if (options.KeyboardFocusKeepsHeaderOpen && context.IsKeyboardFocusWithinHeader)
        {
            return HiddenHeaderHoverDecision.RevealFullHeader;
        }

        return HiddenHeaderHoverDecision.HiddenStrip;
    }
}
