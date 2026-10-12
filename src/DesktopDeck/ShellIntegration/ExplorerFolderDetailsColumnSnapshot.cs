using System;
using System.Collections.Generic;

namespace DesktopDeck.ShellIntegration;

/// <summary>
/// Read-only visible-column snapshot from an already-open File Explorer Details view.
/// CanonicalPath is only for in-memory matching and must not be emitted to diagnostics.
/// </summary>
internal sealed record ExplorerFolderDetailsColumnSnapshot(
    string CanonicalPath,
    uint ExplorerDpi,
    IReadOnlyList<ExplorerFolderDetailsColumn> VisibleColumns);

internal readonly record struct ExplorerFolderDetailsColumn(
    string CanonicalPropertyName,
    uint WidthPixels,
    double WidthDip,
    uint State);

internal enum ExplorerFolderDetailsColumnReadStatus
{
    Success,
    NotWindows,
    RequiresStaThread,
    InvalidPath,
    ShellAutomationUnavailable,
    NoMatchingExplorerWindow,
    MatchingExplorerNotDetails,
    ColumnManagerUnavailable,
    NoVisibleColumns,
}

internal readonly record struct ExplorerFolderDetailsColumnReadResult(
    ExplorerFolderDetailsColumnReadStatus Status,
    ExplorerFolderDetailsColumnSnapshot? Snapshot)
{
    internal bool IsSuccess =>
        Status == ExplorerFolderDetailsColumnReadStatus.Success && Snapshot is not null;

    internal static ExplorerFolderDetailsColumnReadResult Success(
        ExplorerFolderDetailsColumnSnapshot snapshot) =>
        new(ExplorerFolderDetailsColumnReadStatus.Success, snapshot);

    internal static ExplorerFolderDetailsColumnReadResult Unavailable(
        ExplorerFolderDetailsColumnReadStatus status) =>
        new(status, null);
}
