namespace DesktopDeck.Appearance;

internal static class ChameleonActivationPolicy
{
    internal static ChameleonPresentationDecision Decide(
        ChameleonActivationContext context,
        ChameleonActivationOptions options)
    {
        // The existing appearance engine remains authoritative whenever Chameleon is not active
        // for this group, the group is not currently presented, or another transient host mode owns it.
        if (!context.ChameleonEnabled ||
            !context.GroupUsesChameleon ||
            !context.IsGroupPresented ||
            context.DeferToHostPresentation)
        {
            return ChameleonPresentationDecision.DeferToExistingPolicy;
        }

        if (options.ShowGroupsInFullColorOnMouseover && context.IsPointerOverGroup)
        {
            return ChameleonPresentationDecision.ShowFullColor;
        }

        if (options.ShowDesktopInFullColorWhenInUse && context.IsDesktopOrDesktopDeckForeground)
        {
            return ChameleonPresentationDecision.ShowFullColor;
        }

        return ChameleonPresentationDecision.KeepBlended;
    }
}
