using System;

namespace DesktopDeck.FolderPortals;

public interface IPortalContextOpenHost
{
    bool IsSafeMode { get; }
    bool MasterEnabled { get; }
    bool CanOpenItems { get; }
    bool IsSelectedTabFolderPortal { get; }
    bool IsSingleSelection { get; }
    bool SelectedItemIsDirectory { get; }

    /// <summary>
    /// Existing DesktopDeck command that opens the selected folder in a separate Explorer window.
    /// It must not navigate the Folder Portal itself.
    /// </summary>
    bool TryOpenSelectedFolderInExplorer();

    /// <summary>
    /// Invokes the currently selected native IContextMenu command without routing it through
    /// Folder Portal double-click/Enter navigation logic.
    /// </summary>
    bool TryInvokeRawShellContextCommand();
}

public sealed class PortalContextOpenCoordinator
{
    private readonly IPortalContextOpenHost _host;

    public PortalContextOpenCoordinator(IPortalContextOpenHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public PortalContextOpenDecision Decide(string? canonicalVerb) =>
        PortalContextOpenPolicy.Decide(BuildContext(canonicalVerb));

    /// <summary>
    /// Executes one native context-menu command. The caller obtains canonicalVerb from the
    /// already-hosted IContextMenu via GetCommandString(GCS_VERBW) when available.
    /// </summary>
    public bool Execute(string? canonicalVerb)
    {
        var decision = Decide(canonicalVerb);
        if (decision != PortalContextOpenDecision.OpenFolderInExplorer)
        {
            return _host.TryInvokeRawShellContextCommand();
        }

        // Context-menu Open on a Portal folder intentionally ignores the Portal's
        // navigation preference. The user explicitly chose Shell "Open".
        if (_host.TryOpenSelectedFolderInExplorer())
        {
            return true;
        }

        // If DesktopDeck's dedicated Explorer-open command cannot dispatch, preserve the
        // native Shell command rather than falling back to Portal-internal navigation.
        return _host.TryInvokeRawShellContextCommand();
    }

    private PortalContextOpenContext BuildContext(string? canonicalVerb) => new(
        IsContextMenuInvocation: true,
        IsFolderPortal: _host.IsSelectedTabFolderPortal,
        IsSingleSelection: _host.IsSingleSelection,
        SelectedItemIsDirectory: _host.SelectedItemIsDirectory,
        IsSafeMode: _host.IsSafeMode,
        MasterEnabled: _host.MasterEnabled,
        CanOpenItems: _host.CanOpenItems,
        CanonicalVerb: canonicalVerb);
}
