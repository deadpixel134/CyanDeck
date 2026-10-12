namespace DesktopDeck.Appearance;

internal enum HeaderControlMasterDecision
{
    DeferToExistingPolicy,
    HideAllControls,
}

internal readonly record struct HeaderControlMasterContext(
    bool IsHeaderPresented,
    bool IsGroupInteractive,
    bool DeferToHostPresentation);

internal readonly record struct HeaderControlMasterOptions(bool ShowControlsInGroupHeaders)
{
    // Migration-safe: preserve DesktopDeck's existing v1.9/v7.8 control presentation.
    internal static HeaderControlMasterOptions Default => new(ShowControlsInGroupHeaders: true);
}
