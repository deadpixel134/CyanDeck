using System;
using System.Collections.Generic;

namespace CyanDeck.ShellIntegration;

/// <summary>
/// Read-only snapshot of an already-open File Explorer folder view.
/// The path is used only for in-memory matching and must never be written to diagnostics.
/// </summary>
internal sealed record ExplorerFolderViewSnapshot(
    string CanonicalPath,
    ExplorerFolderViewMode ViewMode,
    int IconSize,
    IReadOnlyList<ExplorerSortColumn> SortColumns);

internal readonly record struct ExplorerSortColumn(
    string CanonicalPropertyName,
    ExplorerSortDirection Direction);

internal enum ExplorerSortDirection
{
    Descending = -1,
    Ascending = 1,
}

/// <summary>
/// Values are the Win32 FOLDERVIEWMODE constants.
/// </summary>
internal enum ExplorerFolderViewMode : int
{
    Auto = -1,
    Icon = 1,
    SmallIcon = 2,
    List = 3,
    Details = 4,
    Thumbnail = 5,
    Tile = 6,
    Thumbstrip = 7,
    Content = 8,
}
