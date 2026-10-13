using CyanDeck.Models;

namespace CyanDeck.Services;

public static class FenceFactory
{
    public static DeckFence Duplicate(DeckFence source, double offsetX = 24, double offsetY = 24)
    {
        var clone = CloneFence(source, includeCollectionItems: true);
        clone.Id = Guid.NewGuid();
        clone.Title = string.IsNullOrWhiteSpace(source.Title) ? "Group copy" : $"{source.Title} copy";
        clone.X = source.X + offsetX;
        clone.Y = source.Y + offsetY;
        clone.Locked = false;
        clone.SelectedTabIndex = Math.Clamp(source.SelectedTabIndex, 0, Math.Max(0, clone.Tabs.Count - 1));
        return clone;
    }

    public static DeckGroupTemplate CreateTemplate(DeckFence source, string name)
        => new()
        {
            Name = name.Trim(),
            Prototype = CloneFence(source, includeCollectionItems: false)
        };

    public static DeckFence CreateFromTemplate(DeckGroupTemplate template, double x, double y, int pageIndex)
    {
        var clone = CloneFence(template.Prototype, includeCollectionItems: false);
        clone.Id = Guid.NewGuid();
        clone.X = x;
        clone.Y = y;
        clone.PageIndex = Math.Max(0, pageIndex);
        clone.Locked = false;
        clone.RolledUp = false;
        clone.RollupDockEdge = DeckRollupDockEdge.None;
        clone.SelectedTabIndex = Math.Clamp(clone.SelectedTabIndex, 0, Math.Max(0, clone.Tabs.Count - 1));
        if (string.IsNullOrWhiteSpace(clone.Title)) clone.Title = template.Name;
        return clone;
    }

    private static DeckFence CloneFence(DeckFence source, bool includeCollectionItems)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = source.Title,
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            ExpandedHeight = source.ExpandedHeight,
            RolledUp = source.RolledUp,
            RollupDockEdge = source.RollupDockEdge,
            AlwaysVisible = source.AlwaysVisible,
            ShowOnAllPages = source.ShowOnAllPages,
            Locked = source.Locked,
            MoveTitlebarToBottomNearTop = source.MoveTitlebarToBottomNearTop,
            PageIndex = source.PageIndex,
            AutoFade = source.AutoFade,
            Opacity = source.Opacity,
            Background = source.Background,
            Accent = source.Accent,
            CornerRadius = source.CornerRadius,
            IconSize = source.IconSize,
            IconSpacingX = source.IconSpacingX,
            IconSpacingY = source.IconSpacingY,
            IconLabelMode = source.IconLabelMode,
            IconLayoutMode = source.IconLayoutMode,
            RollupActivation = source.RollupActivation,
            RollupHoverDelayMs = source.RollupHoverDelayMs,
            IconTint = source.IconTint,
            TitleFontFamily = source.TitleFontFamily,
            TitleFontSize = source.TitleFontSize,
            TitleForeground = source.TitleForeground,
            IconTintStrength = source.IconTintStrength,
            TabStyle = source.TabStyle,
            TitlebarMode = source.TitlebarMode,
            SelectedTabIndex = source.SelectedTabIndex,
            Tabs = source.Tabs.Select(tab => CloneTab(tab, includeCollectionItems)).ToList()
        };

    private static DeckTab CloneTab(DeckTab source, bool includeCollectionItems)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = source.Title,
            Kind = source.Kind,
            PortalPath = source.PortalPath,
            PortalRootPath = source.PortalRootPath,
            NavigateFoldersInPortal = source.NavigateFoldersInPortal,
            ShowPortalFolderIcon = source.ShowPortalFolderIcon,
            InheritPortalViewMode = source.InheritPortalViewMode,
            SyncOpenExplorerView = source.SyncOpenExplorerView,
            SyncExplorerViewMode = source.SyncExplorerViewMode,
            SyncExplorerIconSize = source.SyncExplorerIconSize,
            SyncExplorerSort = source.SyncExplorerSort,
            SyncExplorerDetailsColumns = source.SyncExplorerDetailsColumns,
            SyncExplorerDetailsColumnWidths = source.SyncExplorerDetailsColumnWidths,
            ShowDetailsHeader = source.ShowDetailsHeader,
            DetailsColumns = DeckDetailsColumnState.Normalize(source.DetailsColumns).Select(x => x.Clone()).ToList(),
            PortalViewStates = source.PortalViewStates.ToDictionary(
                pair => pair.Key,
                pair => new DeckPortalViewState
                {
                    ViewMode = pair.Value.ViewMode,
                    ListOrientation = pair.Value.ListOrientation,
                    ShowDetailsHeader = pair.Value.ShowDetailsHeader,
                    DetailsColumns = DeckDetailsColumnState.Normalize(pair.Value.DetailsColumns).Select(x => x.Clone()).ToList()
                },
                StringComparer.OrdinalIgnoreCase),
            Sort = source.Sort,
            SortDescending = source.SortDescending,
            FoldersFirst = source.FoldersFirst,
            ViewMode = source.ViewMode,
            ListOrientation = source.ListOrientation,
            Accent = source.Accent,
            RulePriority = source.RulePriority,
            RuleMatchMode = source.RuleMatchMode,
            Items = source.Kind == DeckTabKind.Collection && includeCollectionItems
                ? source.Items.Select(CloneItem).ToList()
                : [],
            Rules = source.Rules.Select(rule => new DeckRule
            {
                Enabled = rule.Enabled,
                Field = rule.Field,
                Pattern = rule.Pattern
            }).ToList()
        };

    private static DeckItem CloneItem(DeckItem source)
        => new()
        {
            Id = Guid.NewGuid(),
            Path = source.Path,
            DisplayNameOverride = source.DisplayNameOverride,
            AddedUtc = source.AddedUtc,
            PositionX = source.PositionX,
            PositionY = source.PositionY
        };
}
