namespace DesktopDeck.Appearance;

internal enum DesktopDeckTitleBarVisibility
{
    Always,
    Mouseover,
    Hidden,
}

internal enum HiddenHeaderHoverDecision
{
    DeferToExistingPolicy,
    HiddenStrip,
    RevealFullHeader,
}

internal readonly record struct HiddenHeaderHoverContext(
    DesktopDeckTitleBarVisibility TitleBarVisibility,
    bool IsFolderPortalGroup,
    int TabCount,
    bool IsPointerOverGroup,
    bool IsKeyboardFocusWithinHeader,
    bool IsGroupInteractive,
    bool IsRolledUp,
    bool IsHeaderPresented);

internal readonly record struct HiddenHeaderHoverOptions(
    bool RevealHiddenHeadersForFolderPortalsAndTabGroups,
    bool KeyboardFocusKeepsHeaderOpen)
{
    // Fences-compatible behavior: Hidden/Never still exposes Folder Portal and tab-group headers on hover.
    internal static HiddenHeaderHoverOptions Default => new(
        RevealHiddenHeadersForFolderPortalsAndTabGroups: true,
        KeyboardFocusKeepsHeaderOpen: true);
}
