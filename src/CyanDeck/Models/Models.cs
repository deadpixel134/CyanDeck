using CyanDeck.Peek;
using CyanDeck.Animations;
using CyanDeck.Automation;
using System.Text.Json.Serialization;

namespace CyanDeck.Models;

public enum DeckTabKind { Collection, FolderPortal }
public enum DeckSortMode { Name, Type, Modified, Added, Size, OpenCount }
public enum DeckTabStyle { MenuOnly, Flat, Segmented, Rounded }
public enum DeckRollupActivation { Hover, DelayedHover, Click }
public enum DeckRollupDockEdge { None, Top, Bottom }
public enum DeckIconLayoutMode { Grid, Free }
public enum DeckItemViewMode { Grid, List, Details }
public enum DeckDetailsColumnKind { Name, Modified, Type, Size }
public enum DeckListOrientation { Vertical, Horizontal }
public enum DeckIconLabelMode { Always, Mouseover, Hidden }
public enum DeckHotkeyActivation { Toggle, PressAndHold }
public enum DeckQuickHideMode { AllDesktop, UngroupedIconsOnly, GroupsOnly }
public enum DeckLayoutAdjustmentMode { None, Scale, PerMonitor }
public enum DeckCreationMouseButtonMode { LeftOnly, RightOnly, Either }
public enum DeckTitlebarMode { Always, Mouseover, Hidden }
public enum DeckRuleMatchMode { Any, All }
public enum RuleField
{
    Extension,
    NameContains,
    NameDoesNotContain,
    NameExact,
    NameStartsWith,
    NameEndsWith,
    PathContains,
    Folder,
    Shortcut,
    Executable,
    FileSizeAtLeastMB,
    FileSizeAtMostMB,
    ModifiedWithinDays,
    CreatedWithinDays,
    CreatedDayOfWeek,
    CreatedTimeRange,
    ShortcutTargetContains,
    ShortcutTargetDoesNotContain,
    ShortcutTargetStartsWith,
    ShortcutTargetEndsWith,
    IconTypeIs,
    IconTypeIsNot,
    FileCategoryIs,
    FileCategoryIsNot
}

public sealed class DeckState
{
    public int Version { get; set; } = 35;
    public int CurrentPage { get; set; }
    public List<DeckMonitorLayoutProfile> MonitorProfiles { get; set; } = [];
    public DeckSettings Settings { get; set; } = new();
    public List<DeckFence> Fences { get; set; } = [];
    public List<DeckLayoutSnapshot> Snapshots { get; set; } = [];
    // v7.33 compatibility list. v7.34 migrates these paths into LooseDesktopItems with stable IDs.
    public List<string> UngroupedDesktopPaths { get; set; } = [];
    public List<DeckItem> LooseDesktopItems { get; set; } = [];
    public List<DeckGroupTemplate> Templates { get; set; } = [];
}

public sealed class DeckSettings
{
    // Settings dialog edits a detached copy; the shared state is swapped only
    // while the persistence checkpoint is attempted. All current members are
    // scalar/value/immutable string properties (no mutable collection fields).
    public DeckSettings Clone() => (DeckSettings)MemberwiseClone();

    // FenceWindow and CalendarModule keep a reference to the shared settings.
    // Copy values IN PLACE, never swap DeckState.Settings to a new instance.
    public void CopyFrom(DeckSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var property in typeof(DeckSettings).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(this, property.GetValue(source));
        }
    }

    // Independent feature switches: the calendar can operate without the Deck shell hooks.
    public bool EnableCyanDeck { get; set; } = true;
    // Both CyanDeck modules start enabled for a clean first launch.
    // Explicitly saved false values in layout.json are still honored.
    public bool EnableCalendar { get; set; } = true;
    public double CalendarLeft { get; set; } = 120;
    public double CalendarTop { get; set; } = 110;
    public double CalendarOpacity { get; set; } = 0.86;
    public double CalendarWidth { get; set; } = 650;
    public double CalendarHeight { get; set; } = 510;
    // Rolling: keep the anchor date in the middle week. Month: show the whole month.
    // These defaults are also used for pre-v23 v35 setting files without these keys.
    public string CalendarViewMode { get; set; } = "Rolling";
    public int CalendarVisibleWeekRows { get; set; } = 6;
    public bool CalendarChameleonEnabled { get; set; } = true;
    public string CalendarBackgroundColor { get; set; } = "#181C25";
    public double CalendarIdleOpacity { get; set; } = 0.38;
    public double CalendarActiveBackgroundOpacity { get; set; } = 0.86;
    // v29 additive display preferences; old schema-v35 layouts retain their former sizes.
    public double CalendarDateFontSize { get; set; } = 12;
    public double CalendarEventFontSize { get; set; } = 11;
    public string GoogleCalendarClientId { get; set; } = "";
    public bool HideNativeDesktopIcons { get; set; } = true;
    // v26 additive preferences: absent keys in schema-v35 files retain existing standalone
    // visibility and enable the in-group shortcut without discarding user layouts.
    public bool ShowStandaloneRecycleBin { get; set; } = true;
    public bool ShowRecycleBinInSystemGroup { get; set; } = true;
    // Position of the CyanDeck-managed Recycle Bin surrogate that remains visible when
    // Explorer's native desktop ListView is hidden. Nullable values use automatic placement.
    public double? RecycleBinPositionX { get; set; }
    public double? RecycleBinPositionY { get; set; }
    public bool AutoImportDesktop { get; set; } = true;
    // v7.44: false preserves the pre-v7.44 automatic category/rule placement behavior.
    // When true, explicit rules still win and unmatched newly observed desktop items
    // use DefaultDesktopPlacementKind/GroupId/TabId.
    public bool UseDefaultDesktopPlacement { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DefaultDesktopPlacementKind DefaultDesktopPlacementKind { get; set; } = DefaultDesktopPlacementKind.Desktop;
    public string? DefaultDesktopPlacementGroupId { get; set; }
    public string? DefaultDesktopPlacementTabId { get; set; }
    public bool ActivateDefaultDestinationTab { get; set; }
    // Compatibility mirror for v7.33-v7.43 backups and older tests/UI paths.
    public bool LeaveNewDesktopItemsUngrouped { get; set; }
    public bool ShowFilePreviews { get; set; } = true;
    public bool HideFileShortcutBadges { get; set; }
    public bool HideInternetShortcutBadges { get; set; }
    public bool ShowShortcutBadgesInFolderPortals { get; set; }
    public bool TrackDesktopLocationChanges { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool EnableChameleon { get; set; } = true;
    public bool EnableRollup { get; set; } = true;
    public bool EnableRollupDoubleClick { get; set; } = true;
    public bool EnableRollupEdgeDock { get; set; } = true;
    public double ChameleonIdleOpacity { get; set; } = 0.38;
    // Independent transparency of ungrouped CyanDeck-managed icons.
    // Disabled by default so existing standalone icons retain their original appearance;
    // the Recycle Bin falls back to its existing group-Chameleon fade until opted in.
    public bool StandaloneIconChameleonEnabled { get; set; }
    public double StandaloneIconIdleOpacity { get; set; } = 0.38;
    public double StandaloneIconHoverOpacity { get; set; } = 1.0;
    public bool ApplyStandaloneIconChameleonToRecycleBin { get; set; }
    public string PeekHotkeyLabel { get; set; } = "Win+Space";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckHotkeyActivation PeekActivation { get; set; } = DeckHotkeyActivation.Toggle;
    public bool EnableDesktopDisplay { get; set; } = true;
    public string DesktopDisplayHotkeyLabel { get; set; } = "Win+D";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckHotkeyActivation DesktopDisplayActivation { get; set; } = DeckHotkeyActivation.Toggle;
    public string QuickHideHotkeyLabel { get; set; } = "Ctrl+Alt+D";
    public bool EnableQuickHide { get; set; } = true;
    public bool QuickHideAlwaysShowIconsAtStartup { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckQuickHideMode QuickHideMode { get; set; } = DeckQuickHideMode.AllDesktop;
    public int MaxLayoutSnapshots { get; set; } = 20;
    public bool EnableAutomaticDailySnapshots { get; set; } = true;
    public int AutomaticSnapshotRetentionDays { get; set; } = 7;
    public bool CaptureSnapshotScreenPreviews { get; set; }
    public bool CaptureAutomaticSnapshotScreenPreviews { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckLayoutAdjustmentMode LayoutAdjustmentMode { get; set; } = DeckLayoutAdjustmentMode.PerMonitor;
    public bool AnimatePageTransitions { get; set; } = true;
    public int PageTransitionDurationMs { get; set; } = 180;
    public bool EnhancedDesktopPageEasing { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DesktopPageEasingProfile DesktopPageEasingProfile { get; set; } = DesktopPageEasingProfile.Smooth;
    public bool EnableEdgePageGestures { get; set; } = true;
    public bool AllowInteriorMonitorSeamPageNavigation { get; set; }
    public bool EnableTouchPageGestures { get; set; } = true;
    public bool EnableTouchIconFlick { get; set; }
    public bool EnablePageKeyboardNavigation { get; set; } = true;
    public bool EnablePageAltMouseWheel { get; set; } = true;
    public bool EnablePageMiddleDrag { get; set; } = true;
    public bool EnablePageBothButtonsDrag { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckCreationMouseButtonMode CreateGroupMouseButtonMode { get; set; } = DeckCreationMouseButtonMode.RightOnly;
    public bool UnhideWhenSwitchingPages { get; set; } = true;
    public bool BouncePageTransitions { get; set; } = true;
    public bool EnableSmartGroupSnapping { get; set; } = true;
    // v7.49: optionally use the CyanDeck-managed loose desktop spacing as the effective base spacing inside Grid-like group surfaces.
    public bool UseSameSpacingInsideAndOutsideGroups { get; set; }
    public int GroupSnapGap { get; set; } = 12;
    public bool PeekRespectWorkingArea { get; set; } = true;
    public bool PeekHotCornerEnabled { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PeekHotCornerAction PeekHotCornerLeftAction { get; set; } = PeekHotCornerAction.ShowFences;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PeekHotCornerAction PeekHotCornerRightAction { get; set; } = PeekHotCornerAction.ShowFullDesktop;
    public int PeekHotCornerHitThicknessPx { get; set; } = 10;
    public bool PeekTaskbarButtonEnabled { get; set; }
    public string PeekTaskbarCustomIconPath { get; set; } = "";
    public bool EnableTabGroups { get; set; } = true;
    public bool ConfirmTabGroupMerge { get; set; }
    // Click selection is the default. An existing opt-in to hover is persisted unchanged.
    public bool SwitchTabsOnHover { get; set; } = false;
    public int PersistDebounceMs { get; set; } = 450;
    public bool ProtectMultiSelectionDuringDrag { get; set; } = true;
    public bool SnapGroupSizeToIconMultiples { get; set; }
    public bool EnableAsyncItemLoading { get; set; } = true;
    public int AsyncItemLoadThreshold { get; set; } = 60;
    public int IconCacheMaxEntries { get; set; } = 1024;
    public bool DeferHiddenPageRendering { get; set; } = true;
    public bool ShowTitlebarControlsOnMouseover { get; set; }
    public bool ShowTitlebarControlsInInactiveTabs { get; set; }
    public bool ShowControlsInGroupHeaders { get; set; } = true;
    public bool ChameleonFullColorOnMouseover { get; set; } = true;
    public bool ChameleonFullColorWhenDesktopInUse { get; set; }
    public bool KeepRulesUpdatedAtAllTimes { get; set; } = true;
    public bool BlurWallpaperBehindGroups { get; set; }
    public bool HideScrollbarsWhenInactive { get; set; }
    public bool AnimateItemChanges { get; set; } = true;
    // v7.47: FLIP-style per-icon movement animation. Kept opt-in for migration safety.
    public bool AnimateIconsWhenMoved { get; set; }
    public int IconMovementAnimationDurationMs { get; set; } = 160;
    public int IconMovementAnimationMaxItems { get; set; } = 128;
    public bool ConfirmLargeBatchOperations { get; set; } = true;
    public int LargeBatchOperationThreshold { get; set; } = 20;
    public int PortalHistoryLimit { get; set; } = 50;
    public bool EnableCrashLoopProtection { get; set; } = true;
    public int CrashLoopThreshold { get; set; } = 3;
    public int CrashLoopWindowMinutes { get; set; } = 10;
    public bool EnableAutomaticUpdateChecks { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; } = true;
    public string UpdateManifestSource { get; set; } = "";
    public DateTime? LastUpdateCheckUtc { get; set; }
    public bool RequireSignedUpdateManifests { get; set; } = true;
    public string UpdatePublicKeyPath { get; set; } = "";
}

public sealed class DeckFence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "새 그룹";
    public double X { get; set; } = 48;
    public double Y { get; set; } = 48;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 310;
    public double ExpandedHeight { get; set; } = 310;
    public bool RolledUp { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckRollupDockEdge RollupDockEdge { get; set; }
    public bool AlwaysVisible { get; set; }
    public bool ShowOnAllPages { get; set; }
    public bool Locked { get; set; }
    public bool MoveTitlebarToBottomNearTop { get; set; }
    public int PageIndex { get; set; }
    public bool AutoFade { get; set; } = true;
    public double Opacity { get; set; } = 0.74;
    public string Background { get; set; } = "#17191E";
    public string Accent { get; set; } = "#7AA2FF";
    public double CornerRadius { get; set; } = 12;
    public double IconSize { get; set; } = 46;
    public double IconSpacingX { get; set; } = 14;
    public double IconSpacingY { get; set; } = 12;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckIconLabelMode IconLabelMode { get; set; } = DeckIconLabelMode.Always;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckIconLayoutMode IconLayoutMode { get; set; } = DeckIconLayoutMode.Grid;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckRollupActivation RollupActivation { get; set; } = DeckRollupActivation.DelayedHover;
    public int RollupHoverDelayMs { get; set; } = 380;
    public string IconTint { get; set; } = "#FFFFFF";
    public string TitleFontFamily { get; set; } = "Segoe UI";
    public double TitleFontSize { get; set; } = 12;
    // Each group has its own item-label text size, separate from its title font.
    public double IconLabelFontSize { get; set; } = 11.5;
    public string TitleForeground { get; set; } = "#F4F6FA";
    public double IconTintStrength { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckTabStyle TabStyle { get; set; } = DeckTabStyle.Rounded;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckTitlebarMode TitlebarMode { get; set; } = DeckTitlebarMode.Always;
    public int SelectedTabIndex { get; set; }
    public List<DeckTab> Tabs { get; set; } = [];
}

public sealed class DeckTab
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "항목";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckTabKind Kind { get; set; } = DeckTabKind.Collection;
    public string? PortalPath { get; set; }
    public string? PortalRootPath { get; set; }
    public bool NavigateFoldersInPortal { get; set; } = true;
    public bool ShowPortalFolderIcon { get; set; } = true;
    public bool InheritPortalViewMode { get; set; } = true;
    public bool SyncOpenExplorerView { get; set; }
    public bool SyncExplorerViewMode { get; set; } = true;
    public bool SyncExplorerIconSize { get; set; } = true;
    public bool SyncExplorerSort { get; set; } = true;
    public bool SyncExplorerDetailsColumns { get; set; }
    public bool SyncExplorerDetailsColumnWidths { get; set; }
    public bool ShowDetailsHeader { get; set; } = true;
    public List<DeckDetailsColumnState> DetailsColumns { get; set; } = DeckDetailsColumnState.CreateDefault();
    public Dictionary<string, DeckPortalViewState> PortalViewStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckSortMode Sort { get; set; } = DeckSortMode.Name;
    public bool SortDescending { get; set; }
    public bool FoldersFirst { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckItemViewMode ViewMode { get; set; } = DeckItemViewMode.Grid;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckListOrientation ListOrientation { get; set; } = DeckListOrientation.Vertical;
    public string? Accent { get; set; }
    public int RulePriority { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckRuleMatchMode RuleMatchMode { get; set; } = DeckRuleMatchMode.Any;
    public List<DeckItem> Items { get; set; } = [];
    public List<DeckRule> Rules { get; set; } = [];
}

public sealed class DeckPortalViewState
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckItemViewMode ViewMode { get; set; } = DeckItemViewMode.Grid;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckListOrientation ListOrientation { get; set; } = DeckListOrientation.Vertical;
    public bool ShowDetailsHeader { get; set; } = true;
    public List<DeckDetailsColumnState> DetailsColumns { get; set; } = DeckDetailsColumnState.CreateDefault();
}

public sealed class DeckDetailsColumnState
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckDetailsColumnKind Kind { get; set; }
    public bool IsVisible { get; set; } = true;
    public int DisplayOrder { get; set; }
    public double? WidthDip { get; set; }

    public DeckDetailsColumnState Clone() => new()
    {
        Kind = Kind,
        IsVisible = IsVisible,
        DisplayOrder = DisplayOrder,
        WidthDip = WidthDip,
    };

    public static List<DeckDetailsColumnState> CreateDefault() =>
    [
        new() { Kind = DeckDetailsColumnKind.Name, DisplayOrder = 0, IsVisible = true, WidthDip = null },
        new() { Kind = DeckDetailsColumnKind.Modified, DisplayOrder = 1, IsVisible = true, WidthDip = 126 },
        new() { Kind = DeckDetailsColumnKind.Type, DisplayOrder = 2, IsVisible = true, WidthDip = 82 },
        new() { Kind = DeckDetailsColumnKind.Size, DisplayOrder = 3, IsVisible = true, WidthDip = 78 },
    ];

    public static List<DeckDetailsColumnState> Normalize(IEnumerable<DeckDetailsColumnState>? source)
    {
        var defaults = CreateDefault();
        var byKind = new Dictionary<DeckDetailsColumnKind, DeckDetailsColumnState>();
        if (source is not null)
        {
            foreach (var item in source)
            {
                if (item is null || byKind.ContainsKey(item.Kind)) continue;
                var clone = item.Clone();
                if (clone.WidthDip is double width)
                {
                    clone.WidthDip = double.IsFinite(width) && width > 0
                        ? Math.Clamp(width, 48, 640)
                        : null;
                }
                byKind[clone.Kind] = clone;
            }
        }

        foreach (var fallback in defaults)
            byKind.TryAdd(fallback.Kind, fallback);

        // Name is CyanDeck's primary identity column and must always remain reachable.
        byKind[DeckDetailsColumnKind.Name].IsVisible = true;

        var visible = byKind.Values
            .Where(x => x.IsVisible)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => (int)x.Kind)
            .ToArray();
        for (int i = 0; i < visible.Length; i++) visible[i].DisplayOrder = i;

        foreach (var hidden in byKind.Values.Where(x => !x.IsVisible))
            hidden.DisplayOrder = int.MaxValue;

        return byKind.Values.OrderBy(x => (int)x.Kind).ToList();
    }
}

public sealed class DeckItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public string? DisplayNameOverride { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }
    public long OpenCount { get; set; }
    public bool ExcludeFromQuickHide { get; set; }
    // User-pinned loose desktop icons stay outside automatic classification until the
    // user explicitly sends them back to a group (auto-classify or touch flick).
    public bool PinnedToDesktop { get; set; }

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(DisplayNameOverride)
        ? DisplayNameOverride!
        : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) switch
        {
            "" => Path,
            var name => name
        };
}

public sealed class DeckRule
{
    public bool Enabled { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RuleField Field { get; set; }
    public string Pattern { get; set; } = "";
}


public sealed class DeckGroupTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Template";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DeckFence Prototype { get; set; } = new();
}

public sealed class DeckLayoutSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Snapshot";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsAutomatic { get; set; }
    public string MonitorSignature { get; set; } = "";
    public int CurrentPage { get; set; }
    public List<DeckMonitorDescriptor> Monitors { get; set; } = [];
    public List<DeckFencePlacement> Fences { get; set; } = [];
    public List<DeckItemPlacement> Items { get; set; } = [];
}

public sealed class DeckItemPlacement
{
    public Guid TabId { get; set; }
    public Guid ItemId { get; set; }
    public string Path { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
}

public sealed class DeckFencePlacement
{
    public Guid FenceId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double ExpandedHeight { get; set; }
    public bool RolledUp { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeckRollupDockEdge RollupDockEdge { get; set; }
    public bool Locked { get; set; }
    public bool MoveTitlebarToBottomNearTop { get; set; }
    public int SelectedTabIndex { get; set; }
    public int PageIndex { get; set; }
}

public sealed class DeckMonitorLayoutProfile
{
    public string Signature { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<DeckMonitorDescriptor> Monitors { get; set; } = [];
    public List<DeckFencePlacement> Fences { get; set; } = [];
}

public sealed class DeckMonitorDescriptor
{
    public string DeviceName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WorkingX { get; set; }
    public int WorkingY { get; set; }
    public int WorkingWidth { get; set; }
    public int WorkingHeight { get; set; }
    public bool Primary { get; set; }
}
