using System;

namespace CyanDeck.ShellIntegration;

internal enum FolderPortalImportedView
{
    Grid,
    List,
    Details,
}

internal enum FolderPortalImportedSort
{
    Name,
    Type,
    Modified,
    Size,
}

internal readonly record struct FolderPortalExplorerViewImportPlan(
    FolderPortalImportedView? View,
    int? IconSize,
    FolderPortalImportedSort? Sort,
    bool SortDescending)
{
    public bool HasAnyChange => View.HasValue || IconSize.HasValue || Sort.HasValue;
}

/// <summary>
/// Converts a Shell folder view into only the subset CyanDeck already knows how to represent.
/// Unknown or richer Explorer state is intentionally ignored rather than approximated destructively.
/// </summary>
internal static class FolderPortalExplorerViewImportPolicy
{
    internal const int MinimumCyanDeckIconSize = 24;
    internal const int MaximumCyanDeckIconSize = 128;

    internal static FolderPortalExplorerViewImportPlan BuildPlan(
        ExplorerFolderViewSnapshot snapshot,
        bool importViewMode,
        bool importIconSize,
        bool importSort)
    {
        FolderPortalImportedView? mappedView = importViewMode
            ? MapViewMode(snapshot.ViewMode)
            : null;

        int? mappedIconSize = null;
        if (importIconSize && IsGridLike(snapshot.ViewMode) && snapshot.IconSize > 0)
        {
            mappedIconSize = Math.Clamp(
                snapshot.IconSize,
                MinimumCyanDeckIconSize,
                MaximumCyanDeckIconSize);
        }

        FolderPortalImportedSort? mappedSort = null;
        bool descending = false;
        if (importSort)
        {
            foreach (ExplorerSortColumn column in snapshot.SortColumns)
            {
                mappedSort = MapSort(column.CanonicalPropertyName);
                if (mappedSort.HasValue)
                {
                    descending = column.Direction == ExplorerSortDirection.Descending;
                    break;
                }
            }
        }

        return new FolderPortalExplorerViewImportPlan(
            mappedView,
            mappedIconSize,
            mappedSort,
            descending);
    }

    private static FolderPortalImportedView? MapViewMode(ExplorerFolderViewMode mode) => mode switch
    {
        ExplorerFolderViewMode.Icon => FolderPortalImportedView.Grid,
        ExplorerFolderViewMode.SmallIcon => FolderPortalImportedView.Grid,
        ExplorerFolderViewMode.Thumbnail => FolderPortalImportedView.Grid,
        ExplorerFolderViewMode.Tile => FolderPortalImportedView.Grid,
        ExplorerFolderViewMode.Thumbstrip => FolderPortalImportedView.Grid,
        ExplorerFolderViewMode.List => FolderPortalImportedView.List,
        ExplorerFolderViewMode.Content => FolderPortalImportedView.List,
        ExplorerFolderViewMode.Details => FolderPortalImportedView.Details,
        _ => null,
    };

    private static bool IsGridLike(ExplorerFolderViewMode mode) => mode is
        ExplorerFolderViewMode.Icon or
        ExplorerFolderViewMode.SmallIcon or
        ExplorerFolderViewMode.Thumbnail or
        ExplorerFolderViewMode.Tile or
        ExplorerFolderViewMode.Thumbstrip;

    private static FolderPortalImportedSort? MapSort(string canonicalPropertyName) =>
        canonicalPropertyName switch
        {
            "System.ItemNameDisplay" => FolderPortalImportedSort.Name,
            "System.FileName" => FolderPortalImportedSort.Name,
            "System.ItemTypeText" => FolderPortalImportedSort.Type,
            "System.FileExtension" => FolderPortalImportedSort.Type,
            "System.DateModified" => FolderPortalImportedSort.Modified,
            "System.Size" => FolderPortalImportedSort.Size,
            _ => null,
        };
}
