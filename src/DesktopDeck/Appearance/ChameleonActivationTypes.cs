namespace DesktopDeck.Appearance;

internal enum ChameleonPresentationDecision
{
    DeferToExistingPolicy,
    KeepBlended,
    ShowFullColor,
}

internal readonly record struct ChameleonActivationContext(
    bool ChameleonEnabled,
    bool GroupUsesChameleon,
    bool IsPointerOverGroup,
    bool IsDesktopOrDesktopDeckForeground,
    bool IsGroupPresented,
    bool DeferToHostPresentation);

internal readonly record struct ChameleonActivationOptions(
    bool ShowGroupsInFullColorOnMouseover,
    bool ShowDesktopInFullColorWhenInUse)
{
    // Migration-safe: preserve DesktopDeck's existing Chameleon behavior until explicitly enabled.
    internal static ChameleonActivationOptions Default => new(
        ShowGroupsInFullColorOnMouseover: false,
        ShowDesktopInFullColorWhenInUse: false);
}
