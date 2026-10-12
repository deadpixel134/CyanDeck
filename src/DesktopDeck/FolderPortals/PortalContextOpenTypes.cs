namespace DesktopDeck.FolderPortals;

public enum PortalContextOpenDecision
{
    DelegateToExistingShellPipeline = 0,
    OpenFolderInExplorer = 1,
}

public sealed record PortalContextOpenContext(
    bool IsContextMenuInvocation,
    bool IsFolderPortal,
    bool IsSingleSelection,
    bool SelectedItemIsDirectory,
    bool IsSafeMode,
    bool MasterEnabled,
    bool CanOpenItems,
    string? CanonicalVerb);
