using System;
using System.Collections.Generic;

namespace DesktopDeck.ShellIntegration;

internal enum FolderPortalDetailsColumn
{
    Name,
    Modified,
    Type,
    Size,
}

internal readonly record struct FolderPortalDetailsColumnLayoutEntry(
    FolderPortalDetailsColumn Column,
    int DisplayOrder,
    bool IsVisible,
    double? WidthDip);

internal sealed record FolderPortalExplorerDetailsColumnImportPlan(
    IReadOnlyList<FolderPortalDetailsColumnLayoutEntry> Columns,
    bool NameWasForcedVisible)
{
    internal bool HasAnyMappedColumn
    {
        get
        {
            foreach (FolderPortalDetailsColumnLayoutEntry entry in Columns)
            {
                if (entry.IsVisible)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// Projects Explorer's visible Details columns into DesktopDeck's existing four-column model only.
/// Unknown Shell properties are ignored rather than creating new columns or guessing semantics.
/// </summary>
internal static class FolderPortalExplorerDetailsColumnImportPolicy
{
    internal const double MinimumColumnWidthDip = 48;
    internal const double MaximumColumnWidthDip = 640;

    private static readonly FolderPortalDetailsColumn[] SupportedColumns =
    {
        FolderPortalDetailsColumn.Name,
        FolderPortalDetailsColumn.Modified,
        FolderPortalDetailsColumn.Type,
        FolderPortalDetailsColumn.Size,
    };

    internal static FolderPortalExplorerDetailsColumnImportPlan? BuildPlan(
        ExplorerFolderDetailsColumnSnapshot snapshot,
        bool importVisibleSetAndOrder,
        bool importWidths)
    {
        if (!importVisibleSetAndOrder && !importWidths)
        {
            return null;
        }

        var mappedInExplorerOrder = new List<(FolderPortalDetailsColumn Column, double? WidthDip)>();
        var seen = new HashSet<FolderPortalDetailsColumn>();

        foreach (ExplorerFolderDetailsColumn explorerColumn in snapshot.VisibleColumns)
        {
            FolderPortalDetailsColumn? mapped = MapColumn(explorerColumn.CanonicalPropertyName);
            if (!mapped.HasValue || !seen.Add(mapped.Value))
            {
                continue;
            }

            double? width = null;
            if (importWidths && explorerColumn.WidthDip > 0 && double.IsFinite(explorerColumn.WidthDip))
            {
                width = Math.Clamp(
                    explorerColumn.WidthDip,
                    MinimumColumnWidthDip,
                    MaximumColumnWidthDip);
            }

            mappedInExplorerOrder.Add((mapped.Value, width));
        }

        if (mappedInExplorerOrder.Count == 0)
        {
            return null;
        }

        bool nameForcedVisible = false;
        if (!seen.Contains(FolderPortalDetailsColumn.Name))
        {
            mappedInExplorerOrder.Insert(0, (FolderPortalDetailsColumn.Name, null));
            seen.Add(FolderPortalDetailsColumn.Name);
            nameForcedVisible = true;
        }

        var entries = new List<FolderPortalDetailsColumnLayoutEntry>(SupportedColumns.Length);
        int visibleOrder = 0;
        foreach ((FolderPortalDetailsColumn column, double? width) in mappedInExplorerOrder)
        {
            entries.Add(new FolderPortalDetailsColumnLayoutEntry(
                column,
                visibleOrder++,
                IsVisible: true,
                WidthDip: importWidths ? width : null));
        }

        if (importVisibleSetAndOrder)
        {
            foreach (FolderPortalDetailsColumn supported in SupportedColumns)
            {
                if (!seen.Contains(supported))
                {
                    entries.Add(new FolderPortalDetailsColumnLayoutEntry(
                        supported,
                        DisplayOrder: int.MaxValue,
                        IsVisible: false,
                        WidthDip: null));
                }
            }
        }

        return new FolderPortalExplorerDetailsColumnImportPlan(entries, nameForcedVisible);
    }

    internal static FolderPortalDetailsColumn? MapColumn(string canonicalPropertyName) =>
        canonicalPropertyName switch
        {
            "System.ItemNameDisplay" => FolderPortalDetailsColumn.Name,
            "System.FileName" => FolderPortalDetailsColumn.Name,
            "System.DateModified" => FolderPortalDetailsColumn.Modified,
            "System.ItemTypeText" => FolderPortalDetailsColumn.Type,
            "System.FileExtension" => FolderPortalDetailsColumn.Type,
            "System.Size" => FolderPortalDetailsColumn.Size,
            _ => null,
        };
}
