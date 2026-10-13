namespace CyanDeck.Appearance;

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
    bool IsDesktopOrCyanDeckForeground,
    bool IsGroupPresented,
    bool DeferToHostPresentation);

internal readonly record struct ChameleonActivationOptions(
    bool ShowGroupsInFullColorOnMouseover,
    bool ShowDesktopInFullColorWhenInUse)
{
    // Migration-safe: preserve CyanDeck's existing Chameleon behavior until explicitly enabled.
    internal static ChameleonActivationOptions Default => new(
        ShowGroupsInFullColorOnMouseover: false,
        ShowDesktopInFullColorWhenInUse: false);
}
