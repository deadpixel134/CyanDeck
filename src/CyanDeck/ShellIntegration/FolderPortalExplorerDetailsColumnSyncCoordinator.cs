namespace CyanDeck.ShellIntegration;

/// <summary>
/// Event-driven companion to v6.7. Call only from the existing Portal navigation-complete path or
/// the explicit "Sync view from Explorer now" command. It creates a plan and never mutates UI/state itself.
/// </summary>
internal sealed class FolderPortalExplorerDetailsColumnSyncCoordinator
{
    private readonly ExplorerFolderDetailsColumnReader _reader;

    internal FolderPortalExplorerDetailsColumnSyncCoordinator(
        ExplorerFolderDetailsColumnReader? reader = null)
    {
        _reader = reader ?? new ExplorerFolderDetailsColumnReader();
    }

    internal FolderPortalExplorerDetailsColumnSyncResult TryCreatePlan(
        string currentPortalPath,
        FolderPortalExplorerDetailsColumnSyncOptions options)
    {
        if (!options.Enabled || (!options.ImportVisibleSetAndOrder && !options.ImportWidths))
        {
            return FolderPortalExplorerDetailsColumnSyncResult.NotApplied(
                ExplorerFolderDetailsColumnReadStatus.ColumnManagerUnavailable,
                FolderPortalExplorerDetailsColumnSyncReason.Disabled);
        }

        ExplorerFolderDetailsColumnReadResult read = _reader.TryRead(currentPortalPath);
        if (!read.IsSuccess || read.Snapshot is null)
        {
            return FolderPortalExplorerDetailsColumnSyncResult.NotApplied(
                read.Status,
                FolderPortalExplorerDetailsColumnSyncReason.NoUsableExplorerState);
        }

        FolderPortalExplorerDetailsColumnImportPlan? plan =
            FolderPortalExplorerDetailsColumnImportPolicy.BuildPlan(
                read.Snapshot,
                options.ImportVisibleSetAndOrder,
                options.ImportWidths);

        return plan is { HasAnyMappedColumn: true }
            ? FolderPortalExplorerDetailsColumnSyncResult.Applied(plan)
            : FolderPortalExplorerDetailsColumnSyncResult.NotApplied(
                read.Status,
                FolderPortalExplorerDetailsColumnSyncReason.NothingMappable);
    }
}

internal readonly record struct FolderPortalExplorerDetailsColumnSyncOptions(
    bool Enabled,
    bool ImportVisibleSetAndOrder,
    bool ImportWidths)
{
    // v7.17 is deliberately opt-in even for users who previously enabled v6.7 view sync.
    internal static FolderPortalExplorerDetailsColumnSyncOptions Default => new(
        Enabled: false,
        ImportVisibleSetAndOrder: false,
        ImportWidths: false);
}

internal enum FolderPortalExplorerDetailsColumnSyncReason
{
    Applied,
    Disabled,
    NoUsableExplorerState,
    NothingMappable,
}

internal readonly record struct FolderPortalExplorerDetailsColumnSyncResult(
    FolderPortalExplorerDetailsColumnSyncReason Reason,
    ExplorerFolderDetailsColumnReadStatus ReadStatus,
    FolderPortalExplorerDetailsColumnImportPlan? Plan)
{
    internal bool WasApplied => Reason == FolderPortalExplorerDetailsColumnSyncReason.Applied && Plan is not null;

    internal static FolderPortalExplorerDetailsColumnSyncResult Applied(
        FolderPortalExplorerDetailsColumnImportPlan plan) =>
        new(
            FolderPortalExplorerDetailsColumnSyncReason.Applied,
            ExplorerFolderDetailsColumnReadStatus.Success,
            plan);

    internal static FolderPortalExplorerDetailsColumnSyncResult NotApplied(
        ExplorerFolderDetailsColumnReadStatus status,
        FolderPortalExplorerDetailsColumnSyncReason reason) =>
        new(reason, status, null);
}
