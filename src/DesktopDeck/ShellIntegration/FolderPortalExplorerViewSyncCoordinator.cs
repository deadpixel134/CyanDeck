using System;

namespace DesktopDeck.ShellIntegration;

/// <summary>
/// Stateless coordinator intended to be called at an existing Folder Portal navigation-complete point
/// or from an explicit "Sync view from Explorer now" command. It performs no timer/polling work.
/// </summary>
internal sealed class FolderPortalExplorerViewSyncCoordinator
{
    private readonly ExplorerFolderViewReader _reader;

    internal FolderPortalExplorerViewSyncCoordinator(ExplorerFolderViewReader? reader = null)
    {
        _reader = reader ?? new ExplorerFolderViewReader();
    }

    internal FolderPortalExplorerViewSyncResult TryCreatePlan(
        string currentPortalPath,
        FolderPortalExplorerViewSyncOptions options)
    {
        if (!options.Enabled)
        {
            return FolderPortalExplorerViewSyncResult.NotApplied(
                ExplorerFolderViewReadStatus.ViewInterfaceUnavailable,
                FolderPortalExplorerViewSyncReason.Disabled);
        }

        ExplorerFolderViewReadResult read = _reader.TryRead(currentPortalPath);
        if (!read.IsSuccess || read.Snapshot is null)
        {
            return FolderPortalExplorerViewSyncResult.NotApplied(
                read.Status,
                FolderPortalExplorerViewSyncReason.NoUsableExplorerState);
        }

        FolderPortalExplorerViewImportPlan plan = FolderPortalExplorerViewImportPolicy.BuildPlan(
            read.Snapshot,
            options.ImportViewMode,
            options.ImportIconSize,
            options.ImportSort);

        return plan.HasAnyChange
            ? FolderPortalExplorerViewSyncResult.Applied(plan)
            : FolderPortalExplorerViewSyncResult.NotApplied(
                read.Status,
                FolderPortalExplorerViewSyncReason.NothingMappable);
    }
}

internal readonly record struct FolderPortalExplorerViewSyncOptions(
    bool Enabled,
    bool ImportViewMode,
    bool ImportIconSize,
    bool ImportSort)
{
    internal static FolderPortalExplorerViewSyncOptions Default => new(
        Enabled: false,
        ImportViewMode: true,
        ImportIconSize: true,
        ImportSort: true);
}

internal enum FolderPortalExplorerViewSyncReason
{
    Applied,
    Disabled,
    NoUsableExplorerState,
    NothingMappable,
}

internal readonly record struct FolderPortalExplorerViewSyncResult(
    FolderPortalExplorerViewSyncReason Reason,
    ExplorerFolderViewReadStatus ReadStatus,
    FolderPortalExplorerViewImportPlan? Plan)
{
    internal bool WasApplied => Reason == FolderPortalExplorerViewSyncReason.Applied && Plan.HasValue;

    internal static FolderPortalExplorerViewSyncResult Applied(FolderPortalExplorerViewImportPlan plan) =>
        new(FolderPortalExplorerViewSyncReason.Applied, ExplorerFolderViewReadStatus.Success, plan);

    internal static FolderPortalExplorerViewSyncResult NotApplied(
        ExplorerFolderViewReadStatus status,
        FolderPortalExplorerViewSyncReason reason) =>
        new(reason, status, null);
}
