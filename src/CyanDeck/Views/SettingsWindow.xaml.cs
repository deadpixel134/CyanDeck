using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CyanDeck.Appearance;
using CyanDeck.Animations;
using CyanDeck.Automation;
using CyanDeck.Models;
using CyanDeck.Layout;
using CyanDeck.Input;
using CyanDeck.Peek;
using CyanDeck.Services;

namespace CyanDeck.Views;

public partial class SettingsWindow : Window
{
    private readonly DeckState _state;
    private readonly Func<bool> _apply;
    private readonly Action _applyRulesNow;
    private readonly Action _export;
    private readonly Func<bool> _import;
    private readonly Action _resetMonitorLayouts;
    private readonly Action _automaticLayout;
    private readonly Action _swapScreenContents;
    private readonly Action _clearIconCache;
    private readonly Action _validateConfiguration;
    private readonly Action _openSettingsFolder;
    private readonly Action _openDiagnosticLogs;
    private readonly Action _createDiagnosticBundle;
    private readonly Action _openSettingsAuditLog;
    private readonly Action _runSelfTest;
    private readonly Func<Task> _checkForUpdates;
    private readonly Action _rollbackLastUpdate;
    private readonly Action _restartSafeMode;
    private readonly Action _restartNormal;
    private readonly bool _safeMode;
    private readonly bool _compatibilityReadOnly;
    private readonly Func<string, bool> _isManagedSettingLocked;
    private readonly Func<string, bool> _isManagedCapabilityAllowed;
    private readonly Func<string> _getManagedPolicyStatus;
    private readonly Action _reloadManagedPolicy;
    private readonly Action _addSystemIconGroup;
    private readonly Action _resetStandaloneRecycleBinPosition;
    private readonly Func<bool> _factoryReset;
    private readonly Func<bool> _completeLocalReset;
    private readonly Func<bool, bool, bool> _resetPlacement;
    private readonly Func<bool> _resetAllWindowGeometry;
    private readonly Dictionary<Guid, GroupDraft> _drafts = new();
    private DeckFence? _currentFence;
    private bool _loading;

    private sealed record DefaultPlacementChoice(
        string Label,
        bool UsesExplicitDefault,
        DefaultDesktopPlacementKind Kind,
        string? GroupId = null,
        string? TabId = null);

    public SettingsWindow(DeckState state, Func<bool> apply, Action applyRulesNow, Action export, Func<bool> import, Action resetMonitorLayouts, Action automaticLayout, Action swapScreenContents, Action clearIconCache, Action validateConfiguration, Action openSettingsFolder, Action openDiagnosticLogs, Action createDiagnosticBundle, Action openSettingsAuditLog, Action runSelfTest, Func<Task> checkForUpdates, Action rollbackLastUpdate, Action restartSafeMode, Action restartNormal, bool safeMode, bool compatibilityReadOnly, Func<string, bool> isManagedSettingLocked, Func<string, bool> isManagedCapabilityAllowed, Func<string> getManagedPolicyStatus, Action reloadManagedPolicy,
        Action addSystemIconGroup, Action resetStandaloneRecycleBinPosition, Func<bool> factoryReset,
        Func<bool, bool, bool> resetPlacement, Func<bool> resetAllWindowGeometry,
        Func<bool> completeLocalReset)
    {
        _state = state;
        _apply = apply;
        _applyRulesNow = applyRulesNow;
        _export = export;
        _import = import;
        _resetMonitorLayouts = resetMonitorLayouts;
        _automaticLayout = automaticLayout;
        _swapScreenContents = swapScreenContents;
        _clearIconCache = clearIconCache;
        _validateConfiguration = validateConfiguration;
        _openSettingsFolder = openSettingsFolder;
        _openDiagnosticLogs = openDiagnosticLogs;
        _createDiagnosticBundle = createDiagnosticBundle;
        _openSettingsAuditLog = openSettingsAuditLog;
        _runSelfTest = runSelfTest;
        _checkForUpdates = checkForUpdates;
        _rollbackLastUpdate = rollbackLastUpdate;
        _restartSafeMode = restartSafeMode;
        _restartNormal = restartNormal;
        _safeMode = safeMode;
        _compatibilityReadOnly = compatibilityReadOnly;
        _isManagedSettingLocked = isManagedSettingLocked ?? throw new ArgumentNullException(nameof(isManagedSettingLocked));
        _isManagedCapabilityAllowed = isManagedCapabilityAllowed ?? throw new ArgumentNullException(nameof(isManagedCapabilityAllowed));
        _getManagedPolicyStatus = getManagedPolicyStatus ?? throw new ArgumentNullException(nameof(getManagedPolicyStatus));
        _addSystemIconGroup = addSystemIconGroup;
        _resetStandaloneRecycleBinPosition = resetStandaloneRecycleBinPosition;
        _factoryReset = factoryReset;
        _completeLocalReset = completeLocalReset ?? throw new ArgumentNullException(nameof(completeLocalReset));
        _resetPlacement = resetPlacement ?? throw new ArgumentNullException(nameof(resetPlacement));
        _resetAllWindowGeometry = resetAllWindowGeometry ?? throw new ArgumentNullException(nameof(resetAllWindowGeometry));
        _reloadManagedPolicy = reloadManagedPolicy ?? throw new ArgumentNullException(nameof(reloadManagedPolicy));
        InitializeComponent();
        Loaded += (_, _) => { if (SettingsNavigation.SelectedItem is null) NavGeneral.IsSelected = true; };

        var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersionText.Text = $"CyanDeck {appVersion?.Major}.{appVersion?.Minor}.{appVersion?.Build}";
        AboutSchemaText.Text = $"설정 스키마: v{state.Version} · 지원: v{StateSchema.Current}";
        AboutModeText.Text = compatibilityReadOnly
            ? "호환성 보호가 활성화되었습니다. 더 새로운 설정 스키마를 보존했으며, 호환 가능한 백업을 가져오거나 CyanDeck을 업데이트할 때까지 이 세션에서는 레이아웃 상태를 저장하지 않습니다."
            : safeMode
                ? "안전 모드가 활성화되었습니다. 바탕화면 Shell 연결, Windows 바탕화면 아이콘 변경, 파일 감시가 비활성화됩니다."
                : "일반 바탕화면 통합 모드가 활성화되었습니다.";
        RestartNormalButton.IsEnabled = safeMode;
        FactoryResetButton.IsEnabled = !compatibilityReadOnly && !_safeMode &&
            _isManagedCapabilityAllowed("ImportExport.Import") && _isManagedCapabilityAllowed("Settings.EditGeneral");
        CompleteLocalResetButton.IsEnabled = FactoryResetButton.IsEnabled;
        ResetPlacementButton.IsEnabled = !compatibilityReadOnly && !_safeMode;
        ResetAllWindowGeometryButton.IsEnabled = !compatibilityReadOnly && !_safeMode &&
            _isManagedCapabilityAllowed("Layout.MoveResizeGroups") &&
            _isManagedCapabilityAllowed("Settings.EditGeneral");
        ResetGroupGeometryCheck.IsEnabled = _isManagedCapabilityAllowed("Layout.MoveResizeGroups");
        ResetCalendarGeometryCheck.IsEnabled = _isManagedCapabilityAllowed("Settings.EditGeneral");
        SwapScreenContentsButton.IsEnabled = !safeMode && !compatibilityReadOnly;
        AutomaticUpdateCheck.IsChecked = state.Settings.EnableAutomaticUpdateChecks || state.Settings.InstallUpdatesAutomatically;
        InstallUpdatesAutomaticallyCheck.IsChecked = state.Settings.InstallUpdatesAutomatically;
        BetaUpdateChannelCheck.IsChecked = state.Settings.EnableBetaUpdateChannel;
        UpdateManifestSourceBox.Text = state.Settings.UpdateManifestSource ?? "";
        RequireSignedUpdateCheck.IsChecked = true;
        UpdatePublicKeyPathBox.Text = state.Settings.UpdatePublicKeyPath ?? "";
        LastUpdateCheckText.Text = state.Settings.LastUpdateCheckUtc is DateTime lastUpdate
            ? $"마지막 업데이트 확인: {lastUpdate.ToLocalTime():g}"
            : "마지막 업데이트 확인: 없음";
        RefreshUpdateDiagnostics();
        CrashLoopProtectionCheck.IsChecked = state.Settings.EnableCrashLoopProtection;
        CrashLoopThresholdSlider.Value = state.Settings.CrashLoopThreshold;
        CrashLoopWindowSlider.Value = state.Settings.CrashLoopWindowMinutes;
        StartupHealthText.Text = StartupHealthService.GetSnapshot(state.Settings.CrashLoopWindowMinutes, state.Settings.CrashLoopThreshold).Summary;

        DeckModuleCheck.IsChecked = state.Settings.EnableCyanDeck;
        CalendarModuleCheck.IsChecked = state.Settings.EnableCalendar;
        GoogleClientIdBox.Text = state.Settings.GoogleCalendarClientId;
        CalendarWidthBox.Text = state.Settings.CalendarWidth.ToString("F0");
        CalendarHeightBox.Text = state.Settings.CalendarHeight.ToString("F0");
        CalendarViewModeCombo.SelectedIndex = state.Settings.CalendarViewMode == "Month" ? 1 : 0;
        CalendarRowsCombo.Items.Clear();
        for (var n = 3; n <= 8; n++) CalendarRowsCombo.Items.Add(n);
        CalendarRowsCombo.SelectedItem = Math.Clamp(state.Settings.CalendarVisibleWeekRows, 3, 8);
        CalendarChameleonCheck.IsChecked = state.Settings.CalendarChameleonEnabled;
        CalendarBackgroundColorBox.Text = state.Settings.CalendarBackgroundColor;
        CalendarIdleOpacitySlider.Value = Math.Clamp(state.Settings.CalendarIdleOpacity, 0.15, 1);
        CalendarActiveOpacitySlider.Value = Math.Clamp(state.Settings.CalendarActiveBackgroundOpacity, 0.15, 1);
        CalendarDateFontSizeSlider.Value = Math.Clamp(state.Settings.CalendarDateFontSize, 9, 24);
        CalendarEventFontSizeSlider.Value = Math.Clamp(state.Settings.CalendarEventFontSize, 9, 24);
        HideNativeIconsCheck.IsChecked = state.Settings.HideNativeDesktopIcons;
        StandaloneRecycleCheck.IsChecked = state.Settings.ShowStandaloneRecycleBin;
        GroupRecycleCheck.IsChecked = state.Settings.ShowRecycleBinInSystemGroup;
        AutoImportCheck.IsChecked = state.Settings.AutoImportDesktop;
        LeaveNewDesktopItemsCheck.IsChecked = state.Settings.LeaveNewDesktopItemsUngrouped;
        BuildDefaultDesktopPlacementChoices(state.Settings);
        ShowFilePreviewsCheck.IsChecked = state.Settings.ShowFilePreviews;
        HideFileShortcutBadgesCheck.IsChecked = state.Settings.HideFileShortcutBadges;
        HideInternetShortcutBadgesCheck.IsChecked = state.Settings.HideInternetShortcutBadges;
        ShowShortcutBadgesInFolderPortalsCheck.IsChecked = state.Settings.ShowShortcutBadgesInFolderPortals;
        KeepRulesUpdatedCheck.IsChecked = state.Settings.KeepRulesUpdatedAtAllTimes;
        TrackDesktopLocationCheck.IsChecked = state.Settings.TrackDesktopLocationChanges;
        StartupCheck.IsChecked = state.Settings.StartWithWindows;
        EdgeGesturesCheck.IsChecked = state.Settings.EnableEdgePageGestures;
        InteriorSeamGesturesCheck.IsChecked = state.Settings.AllowInteriorMonitorSeamPageNavigation;
        TouchPageGesturesCheck.IsChecked = state.Settings.EnableTouchPageGestures;
        TouchIconFlickCheck.IsChecked = state.Settings.EnableTouchIconFlick;
        PageAltWheelCheck.IsChecked = state.Settings.EnablePageAltMouseWheel;
        PageMiddleDragCheck.IsChecked = state.Settings.EnablePageMiddleDrag;
        PageBothButtonsDragCheck.IsChecked = state.Settings.EnablePageBothButtonsDrag;
        CreateGroupMouseButtonCombo.ItemsSource = new[] { "왼쪽 버튼만", "오른쪽 버튼만", "왼쪽 또는 오른쪽 버튼" };
        CreateGroupMouseButtonCombo.SelectedIndex = state.Settings.CreateGroupMouseButtonMode switch
        {
            DeckCreationMouseButtonMode.LeftOnly => 0,
            DeckCreationMouseButtonMode.Either => 2,
            _ => 1
        };
        UnhideOnPageSwitchCheck.IsChecked = state.Settings.UnhideWhenSwitchingPages;
        AnimatePagesCheck.IsChecked = state.Settings.AnimatePageTransitions;
        EnhancedPageEasingCheck.IsChecked = state.Settings.EnhancedDesktopPageEasing;
        PageEasingProfileCombo.ItemsSource = new[] { "부드럽게", "빠르게", "완만하게" };
        PageEasingProfileCombo.SelectedIndex = state.Settings.DesktopPageEasingProfile switch
        {
            DesktopPageEasingProfile.Snappy => 1,
            DesktopPageEasingProfile.Gentle => 2,
            _ => 0
        };
        BouncePagesCheck.IsChecked = state.Settings.BouncePageTransitions;
        SmartSnapCheck.IsChecked = state.Settings.EnableSmartGroupSnapping;
        SnapGroupSizeCheck.IsChecked = state.Settings.SnapGroupSizeToIconMultiples;
        SnapGapSlider.Value = state.Settings.GroupSnapGap;
        EnableTabGroupsCheck.IsChecked = state.Settings.EnableTabGroups;
        ConfirmTabGroupMergeCheck.IsChecked = state.Settings.ConfirmTabGroupMerge;
        TabSwitchModeCombo.SelectedIndex = state.Settings.SwitchTabsOnHover ? 1 : 0;
        EnableRollupCheck.IsChecked = state.Settings.EnableRollup;
        RollupDoubleClickCheck.IsChecked = state.Settings.EnableRollupDoubleClick;
        RollupEdgeDockCheck.IsChecked = state.Settings.EnableRollupEdgeDock;
        PageDurationSlider.Value = state.Settings.PageTransitionDurationMs;
        ChameleonCheck.IsChecked = state.Settings.EnableChameleon;
        ChameleonFullColorHoverCheck.IsChecked = state.Settings.ChameleonFullColorOnMouseover;
        ChameleonDesktopUseCheck.IsChecked = state.Settings.ChameleonFullColorWhenDesktopInUse;
        BlurWallpaperCheck.IsChecked = state.Settings.BlurWallpaperBehindGroups;
        IdleOpacitySlider.Value = state.Settings.ChameleonIdleOpacity;
        StandaloneIconChameleonCheck.IsChecked = state.Settings.StandaloneIconChameleonEnabled;
        StandaloneIconIdleOpacitySlider.Value = Math.Clamp(state.Settings.StandaloneIconIdleOpacity, 0.15, 1d);
        StandaloneIconHoverOpacitySlider.Value = Math.Clamp(state.Settings.StandaloneIconHoverOpacity, 0.15, 1d);
        StandaloneIconIncludeRecycleCheck.IsChecked = state.Settings.ApplyStandaloneIconChameleonToRecycleBin;
        SnapshotCountSlider.Value = state.Settings.MaxLayoutSnapshots;
        AutomaticSnapshotsCheck.IsChecked = state.Settings.EnableAutomaticDailySnapshots;
        SnapshotScreenPreviewCheck.IsChecked = state.Settings.CaptureSnapshotScreenPreviews;
        AutomaticSnapshotScreenPreviewCheck.IsChecked = state.Settings.CaptureAutomaticSnapshotScreenPreviews;
        AutoSnapshotDaysSlider.Value = state.Settings.AutomaticSnapshotRetentionDays;
        LayoutAdjustmentCombo.ItemsSource = new[]
        {
            "레이아웃을 보존하지 않음",
            "그룹 크기/위치를 비율 조정해 보존",
            "모니터 구성별 레이아웃 저장"
        };
        LayoutAdjustmentCombo.SelectedIndex = (int)state.Settings.LayoutAdjustmentMode;
        PeekWorkAreaCheck.IsChecked = state.Settings.PeekRespectWorkingArea;
        SaveDebounceSlider.Value = state.Settings.PersistDebounceMs;
        ProtectMultiSelectionCheck.IsChecked = state.Settings.ProtectMultiSelectionDuringDrag;
        ShowHeaderControlsCheck.IsChecked = state.Settings.ShowControlsInGroupHeaders;
        ShowTitlebarControlsHoverCheck.IsChecked = state.Settings.ShowTitlebarControlsOnMouseover;
        ShowInactiveTabControlsCheck.IsChecked = state.Settings.ShowTitlebarControlsInInactiveTabs;
        HideInactiveScrollbarsCheck.IsChecked = state.Settings.HideScrollbarsWhenInactive;
        AnimateItemChangesCheck.IsChecked = state.Settings.AnimateItemChanges;
        AnimateIconsWhenMovedCheck.IsChecked = state.Settings.AnimateIconsWhenMoved;
        IconMovementDurationSlider.Value = state.Settings.IconMovementAnimationDurationMs;
        IconMovementMaxItemsSlider.Value = state.Settings.IconMovementAnimationMaxItems;
        ConfirmLargeBatchCheck.IsChecked = state.Settings.ConfirmLargeBatchOperations;
        BatchThresholdSlider.Value = state.Settings.LargeBatchOperationThreshold;
        PortalHistorySlider.Value = state.Settings.PortalHistoryLimit;
        AsyncItemLoadingCheck.IsChecked = state.Settings.EnableAsyncItemLoading;
        AsyncThresholdSlider.Value = state.Settings.AsyncItemLoadThreshold;
        IconCacheSlider.Value = state.Settings.IconCacheMaxEntries;
        DeferHiddenPageRenderingCheck.IsChecked = state.Settings.DeferHiddenPageRendering;
        QuickHideCheck.IsChecked = state.Settings.EnableQuickHide;
        QuickHideStartupShowCheck.IsChecked = state.Settings.QuickHideAlwaysShowIconsAtStartup;
        QuickHideModeCombo.ItemsSource = new[]
        {
            "바탕화면 아이콘과 그룹 모두 숨기기",
            "그룹 밖 아이콘만 숨기기",
            "그룹만 숨기기"
        };
        QuickHideModeCombo.SelectedIndex = (int)state.Settings.QuickHideMode;

        PeekHotCornerLeftCombo.ItemsSource = new[] { "동작 없음", "그룹 표시", "전체 바탕화면 표시" };
        PeekHotCornerLeftCombo.SelectedIndex = (int)state.Settings.PeekHotCornerLeftAction;
        PeekHotCornerRightCombo.ItemsSource = new[] { "동작 없음", "그룹 표시", "전체 바탕화면 표시" };
        PeekHotCornerRightCombo.SelectedIndex = (int)state.Settings.PeekHotCornerRightAction;
        PeekHotCornerCheck.IsChecked = state.Settings.PeekHotCornerEnabled;
        PeekTaskbarButtonCheck.IsChecked = state.Settings.PeekTaskbarButtonEnabled;

        LinkedIconSpacingCheck.IsChecked = state.Settings.UseSameSpacingInsideAndOutsideGroups;
        IconLayoutCombo.ItemsSource = new[] { "격자", "자유 배치" };
        IconLabelModeCombo.ItemsSource = new[] { "항상 표시", "마우스 오버 시 표시", "숨김" };
        TitlebarModeCombo.ItemsSource = new[] { "항상 표시", "마우스 오버 시 표시", "숨김" };
        TitleFontCombo.ItemsSource = new[] { "Segoe UI", "Segoe UI Variable", "Arial", "Consolas" };
        RollupModeCombo.ItemsSource = new[] { "마우스 오버", "지연 후 마우스 오버", "클릭" };
        foreach (var fence in state.Fences) _drafts[fence.Id] = GroupDraft.From(fence);
        FencePicker.ItemsSource = state.Fences;
        FencePicker.DisplayMemberPath = nameof(DeckFence.Title);
        InitializeColorPickers();
        if (state.Fences.Count > 0) FencePicker.SelectedIndex = 0;
        UpdateLabels();
        ApplyManagedPolicyRestrictions();
        UpdateManagedPolicyStatus();
    }

    public void RefreshManagedPolicyState()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var s = _state.Settings;
            StandaloneRecycleCheck.IsChecked = s.ShowStandaloneRecycleBin;
            GroupRecycleCheck.IsChecked = s.ShowRecycleBinInSystemGroup;
            AutoImportCheck.IsChecked = s.AutoImportDesktop;
            SelectDefaultDesktopPlacement(s);
            AnimatePagesCheck.IsChecked = s.AnimatePageTransitions;
            PageDurationSlider.Value = s.PageTransitionDurationMs;
            LayoutAdjustmentCombo.SelectedIndex = (int)s.LayoutAdjustmentMode;
            SnapshotCountSlider.Value = s.MaxLayoutSnapshots;
            AutoSnapshotDaysSlider.Value = s.AutomaticSnapshotRetentionDays;
            ProtectMultiSelectionCheck.IsChecked = s.ProtectMultiSelectionDuringDrag;
            HideInactiveScrollbarsCheck.IsChecked = s.HideScrollbarsWhenInactive;
            AsyncItemLoadingCheck.IsChecked = s.EnableAsyncItemLoading;
            AsyncThresholdSlider.Value = s.AsyncItemLoadThreshold;
            IconCacheSlider.Value = s.IconCacheMaxEntries;
            AnimateIconsWhenMovedCheck.IsChecked = s.AnimateIconsWhenMoved;
            IconMovementDurationSlider.Value = s.IconMovementAnimationDurationMs;
            IconMovementMaxItemsSlider.Value = s.IconMovementAnimationMaxItems;
            LinkedIconSpacingCheck.IsChecked = s.UseSameSpacingInsideAndOutsideGroups;
            CreateGroupMouseButtonCombo.SelectedIndex = s.CreateGroupMouseButtonMode switch
            {
                DeckCreationMouseButtonMode.LeftOnly => 0,
                DeckCreationMouseButtonMode.Either => 2,
                _ => 1
            };
            BatchThresholdSlider.Value = s.LargeBatchOperationThreshold;
            QuickHideModeCombo.SelectedIndex = (int)s.QuickHideMode;
            UpdateLabels();
        }
        finally
        {
            _loading = wasLoading;
        }
        ApplyManagedPolicyRestrictions();
        if (_currentFence is not null && _drafts.TryGetValue(_currentFence.Id, out var managedDraft)) ApplySpacingEditorValues(managedDraft);
        UpdateManagedPolicyStatus();
    }

    private void ApplyManagedPolicyRestrictions()
    {
        GeneralTab.IsEnabled = _isManagedCapabilityAllowed("Settings.EditGeneral");
        InteractionTab.IsEnabled = GeneralTab.IsEnabled;
        AppearanceTab.IsEnabled = GeneralTab.IsEnabled;
        AnimationTab.IsEnabled = GeneralTab.IsEnabled;
        PerformanceTab.IsEnabled = GeneralTab.IsEnabled;
        LayoutTab.IsEnabled = GeneralTab.IsEnabled;
        CalendarTab.IsEnabled = _isManagedCapabilityAllowed("Settings.EditGeneral");
        SystemIconsTab.IsEnabled = _isManagedCapabilityAllowed("Settings.EditGeneral");
        GroupsTab.IsEnabled = _isManagedCapabilityAllowed("Settings.EditGroups");
        SwapScreenContentsButton.IsEnabled = !_safeMode && !_compatibilityReadOnly && _isManagedCapabilityAllowed("Layout.SwapScreenContents");
        ImportConfigurationButton.IsEnabled = _isManagedCapabilityAllowed("ImportExport.Import");
        FactoryResetButton.IsEnabled = !_safeMode && !_compatibilityReadOnly &&
            _isManagedCapabilityAllowed("ImportExport.Import") && _isManagedCapabilityAllowed("Settings.EditGeneral");
        CompleteLocalResetButton.IsEnabled = FactoryResetButton.IsEnabled;
        ResetGroupGeometryCheck.IsEnabled = !_safeMode && !_compatibilityReadOnly &&
            _isManagedCapabilityAllowed("Layout.MoveResizeGroups");
        ResetCalendarGeometryCheck.IsEnabled = !_safeMode && !_compatibilityReadOnly &&
            _isManagedCapabilityAllowed("Settings.EditGeneral");
        ResetPlacementButton.IsEnabled = ResetGroupGeometryCheck.IsEnabled || ResetCalendarGeometryCheck.IsEnabled;
        ResetAllWindowGeometryButton.IsEnabled = ResetGroupGeometryCheck.IsEnabled && ResetCalendarGeometryCheck.IsEnabled;
        AutomaticLayoutButton.IsEnabled = !_safeMode && !_compatibilityReadOnly && _isManagedCapabilityAllowed("Layout.MoveResizeGroups");
        if (GroupEditor is not null)
            GroupEditor.IsEnabled = _currentFence is not null && _isManagedCapabilityAllowed("Settings.EditGroups") && _isManagedCapabilityAllowed("Appearance.Edit");

        if (LinkedIconSpacingCheck is not null)
            LinkedIconSpacingCheck.IsEnabled = !_safeMode && !_compatibilityReadOnly
                && !_isManagedSettingLocked(LinkedIconSpacingIntegrationContract.SettingKey)
                && _isManagedCapabilityAllowed(LinkedIconSpacingIntegrationContract.ManagedCapability);

        TabSwitchModeCombo.IsEnabled = !_safeMode && !_compatibilityReadOnly
            && _isManagedCapabilityAllowed("Settings.EditGeneral")
            && !_isManagedSettingLocked("General.SwitchTabsOnHover");
        StandaloneRecycleCheck.IsEnabled = !_isManagedSettingLocked("General.ShowStandaloneRecycleBin") && _isManagedCapabilityAllowed("Settings.EditGeneral");
        GroupRecycleCheck.IsEnabled = !_isManagedSettingLocked("General.ShowRecycleBinInSystemGroup") && _isManagedCapabilityAllowed("Settings.EditGeneral");
        var standaloneAppearanceEditable = !_safeMode && !_compatibilityReadOnly && _isManagedCapabilityAllowed("Appearance.Edit")
            && _isManagedCapabilityAllowed("Settings.EditGeneral");
        StandaloneIconChameleonCheck.IsEnabled = standaloneAppearanceEditable && !_isManagedSettingLocked("General.StandaloneIconChameleonEnabled");
        StandaloneIconIdleOpacitySlider.IsEnabled = standaloneAppearanceEditable && !_isManagedSettingLocked("General.StandaloneIconIdleOpacity");
        StandaloneIconHoverOpacitySlider.IsEnabled = standaloneAppearanceEditable && !_isManagedSettingLocked("General.StandaloneIconHoverOpacity");
        StandaloneIconIncludeRecycleCheck.IsEnabled = standaloneAppearanceEditable && !_isManagedSettingLocked("General.ApplyStandaloneIconChameleonToRecycleBin");
        AddSystemIconGroupButton.IsEnabled = !_safeMode && !_compatibilityReadOnly && _isManagedCapabilityAllowed("Layout.CreateDeleteGroups");
        ResetStandaloneRecycleButton.IsEnabled = !_safeMode && !_compatibilityReadOnly;
        AutoImportCheck.IsEnabled = !_isManagedSettingLocked("General.AutoImportDesktopItems");
        CreateGroupMouseButtonCombo.IsEnabled = !_isManagedSettingLocked(CreationMouseButtonIntegrationContract.SettingKey) && _isManagedCapabilityAllowed(CreationMouseButtonIntegrationContract.ManagedCapability);
        var defaultPlacementLocked = _isManagedSettingLocked("Automation.UseDefaultDesktopPlacement")
            || _isManagedSettingLocked("Automation.DefaultDesktopPlacementKind")
            || _isManagedSettingLocked("Automation.DefaultDesktopPlacementGroupId")
            || _isManagedSettingLocked("Automation.DefaultDesktopPlacementTabId");
        DefaultDesktopPlacementCombo.IsEnabled = !defaultPlacementLocked;
        ActivateDefaultDestinationTabCheck.IsEnabled = !defaultPlacementLocked
            && !_isManagedSettingLocked("Automation.ActivateDefaultDestinationTab")
            && DefaultDesktopPlacementCombo.SelectedItem is DefaultPlacementChoice { UsesExplicitDefault: true, Kind: DefaultDesktopPlacementKind.CollectionTab };
        AnimatePagesCheck.IsEnabled = !_isManagedSettingLocked("General.PageAnimationsEnabled");
        PageDurationSlider.IsEnabled = !_isManagedSettingLocked("General.PageAnimationDurationMs");
        LayoutAdjustmentCombo.IsEnabled = !_isManagedSettingLocked("General.LayoutAdjustmentMode");
        SnapshotCountSlider.IsEnabled = !_isManagedSettingLocked("General.ManualSnapshotRetention");
        AutoSnapshotDaysSlider.IsEnabled = !_isManagedSettingLocked("General.AutoBackupRetentionDays");
        ProtectMultiSelectionCheck.IsEnabled = !_isManagedSettingLocked("General.PreserveMultiSelectionOnDrag");
        HideInactiveScrollbarsCheck.IsEnabled = !_isManagedSettingLocked("General.HideInactiveScrollbars");
        AsyncItemLoadingCheck.IsEnabled = !_isManagedSettingLocked("Performance.AsyncItemLoading");
        AsyncThresholdSlider.IsEnabled = !_isManagedSettingLocked("Performance.AsyncItemThreshold");
        IconCacheSlider.IsEnabled = !_isManagedSettingLocked("Performance.IconCacheCapacity");
        BatchThresholdSlider.IsEnabled = !_isManagedSettingLocked("Safety.LargeRecycleConfirmThreshold");
        QuickHideCheck.IsEnabled = !_isManagedSettingLocked("QuickHide.Enabled");
        QuickHideStartupShowCheck.IsEnabled = !_isManagedSettingLocked("QuickHide.AlwaysShowIconsAtStartup");
        QuickHideModeCombo.IsEnabled = !_isManagedSettingLocked("General.QuickHideMode");
        ShowInactiveTabControlsCheck.IsEnabled = !_isManagedSettingLocked("Appearance.ShowTitlebarControlsInInactiveTabs")
            && _isManagedCapabilityAllowed("Appearance.Edit");
        HideFileShortcutBadgesCheck.IsEnabled = !_isManagedSettingLocked("Appearance.HideFileShortcutBadges") && _isManagedCapabilityAllowed("Appearance.Edit");
        HideInternetShortcutBadgesCheck.IsEnabled = !_isManagedSettingLocked("Appearance.HideInternetShortcutBadges") && _isManagedCapabilityAllowed("Appearance.Edit");
        ShowShortcutBadgesInFolderPortalsCheck.IsEnabled = !_isManagedSettingLocked("Appearance.ShowShortcutBadgesInFolderPortals") && _isManagedCapabilityAllowed("Appearance.Edit");
        AnimateIconsWhenMovedCheck.IsEnabled = !_isManagedSettingLocked(IconMovementAnimationIntegrationContract.SettingEnabled) && _isManagedCapabilityAllowed(IconMovementAnimationIntegrationContract.ManagedCapability);
        IconMovementDurationSlider.IsEnabled = !_isManagedSettingLocked(IconMovementAnimationIntegrationContract.SettingDurationMs) && _isManagedCapabilityAllowed(IconMovementAnimationIntegrationContract.ManagedCapability);
        IconMovementMaxItemsSlider.IsEnabled = !_isManagedSettingLocked(IconMovementAnimationIntegrationContract.SettingMaxItems) && _isManagedCapabilityAllowed(IconMovementAnimationIntegrationContract.ManagedCapability);
        TouchIconFlickCheck.IsEnabled = !_isManagedSettingLocked("Input.EnableTouchIconFlick")
            && _isManagedCapabilityAllowed("Input.TouchIconFlick");
    }

    private void UpdateManagedPolicyStatus()
    {
        if (ManagedPolicyStatusText is not null) ManagedPolicyStatusText.Text = _getManagedPolicyStatus();
    }

    private void ReloadManagedPolicy_Click(object sender, RoutedEventArgs e)
    {
        _reloadManagedPolicy();
        RefreshManagedPolicyState();
    }

    private void LinkedIconSpacingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (_currentFence is not null && LinkedIconSpacingCheck.IsChecked == true && _drafts.TryGetValue(_currentFence.Id, out var current))
        {
            // Capture any edits made immediately before linking, then show the derived
            // loose-desktop values without overwriting the dormant per-group values.
            _drafts[_currentFence.Id] = current with
            {
                IconSpacingX = SpacingXSlider.Value,
                IconSpacingY = SpacingYSlider.Value
            };
        }
        if (_currentFence is not null && _drafts.TryGetValue(_currentFence.Id, out var draft))
            ApplySpacingEditorValues(draft);
        UpdateLabels();
    }

    private void ApplySpacingEditorValues(GroupDraft draft)
    {
        var linked = LinkedIconSpacingCheck.IsChecked == true;
        if (linked)
        {
            SpacingXSlider.Value = LooseDesktopIconLayoutProfile.Spacing.HorizontalDip;
            SpacingYSlider.Value = LooseDesktopIconLayoutProfile.Spacing.VerticalDip;
        }
        else
        {
            SpacingXSlider.Value = draft.IconSpacingX;
            SpacingYSlider.Value = draft.IconSpacingY;
        }

        var canEditOwnSpacing = !linked
            && !_safeMode
            && !_compatibilityReadOnly
            && _isManagedCapabilityAllowed(LinkedIconSpacingIntegrationContract.ManagedCapability);
        SpacingXSlider.IsEnabled = canEditOwnSpacing;
        SpacingYSlider.IsEnabled = canEditOwnSpacing;
    }

    private void FencePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveCurrentGroupDraft();
        _currentFence = FencePicker.SelectedItem as DeckFence;
        LoadCurrentGroupDraft();
    }

    private void LoadCurrentGroupDraft()
    {
        _loading = true;
        try
        {
            GroupEditor.IsEnabled = _currentFence is not null && _isManagedCapabilityAllowed("Settings.EditGroups") && _isManagedCapabilityAllowed("Appearance.Edit");
            if (_currentFence is null) return;
            var d = _drafts[_currentFence.Id];
            IconLayoutCombo.SelectedIndex = (int)d.IconLayoutMode;
            IconLabelModeCombo.SelectedIndex = (int)d.IconLabelMode;
            TitlebarModeCombo.SelectedIndex = (int)d.TitlebarMode;
            TitleFontCombo.SelectedItem = d.TitleFontFamily;
            if (TitleFontCombo.SelectedItem is null) TitleFontCombo.Text = d.TitleFontFamily;
            TitleFontSizeSlider.Value = d.TitleFontSize;
            IconLabelFontSizeSlider.Value = Math.Clamp(d.IconLabelFontSize, 9, 24);
            TitleColorBox.Text = d.TitleForeground;
            _titlePicker?.SetDisplayedColor(NormalizeRgbHex(d.TitleForeground, "#F4F6FA"));
            IconSizeSlider.Value = d.IconSize;
            ApplySpacingEditorValues(d);
            BackgroundColorBox.Text = d.Background;
            LoadBackgroundEditor(d);
            _backgroundPicker?.SetDisplayedColor(NormalizeRgbHex(d.Background, "#17191E"));
            RollupModeCombo.SelectedIndex = (int)d.RollupActivation;
            HoverDelaySlider.Value = d.RollupHoverDelayMs;
            LockedCheck.IsChecked = d.Locked;
            TitlebarBottomNearTopCheck.IsChecked = d.MoveTitlebarToBottomNearTop;
            UpdateLabels();
        }
        finally { _loading = false; }
    }

    private void SaveCurrentGroupDraft()
    {
        if (_loading || _currentFence is null) return;
        CommitBackgroundHexEditor(revertOnInvalid: true);
        var spacingX = SpacingXSlider.Value;
        var spacingY = SpacingYSlider.Value;
        if (LinkedIconSpacingCheck.IsChecked == true && _drafts.TryGetValue(_currentFence.Id, out var previousSpacingDraft))
        {
            spacingX = previousSpacingDraft.IconSpacingX;
            spacingY = previousSpacingDraft.IconSpacingY;
        }

        _drafts[_currentFence.Id] = new GroupDraft(
            Enum.IsDefined(typeof(DeckIconLayoutMode), IconLayoutCombo.SelectedIndex) ? (DeckIconLayoutMode)IconLayoutCombo.SelectedIndex : DeckIconLayoutMode.Grid,
            Enum.IsDefined(typeof(DeckIconLabelMode), IconLabelModeCombo.SelectedIndex) ? (DeckIconLabelMode)IconLabelModeCombo.SelectedIndex : DeckIconLabelMode.Always,
            Enum.IsDefined(typeof(DeckTitlebarMode), TitlebarModeCombo.SelectedIndex) ? (DeckTitlebarMode)TitlebarModeCombo.SelectedIndex : DeckTitlebarMode.Always,
            string.IsNullOrWhiteSpace(TitleFontCombo.Text) ? "Segoe UI" : TitleFontCombo.Text.Trim(),
            TitleFontSizeSlider.Value,
            IconLabelFontSizeSlider.Value,
            string.IsNullOrWhiteSpace(TitleColorBox.Text) ? "#F4F6FA" : TitleColorBox.Text.Trim(),
            IconSizeSlider.Value,
            spacingX,
            spacingY,
            Enum.IsDefined(typeof(DeckRollupActivation), RollupModeCombo.SelectedIndex) ? (DeckRollupActivation)RollupModeCombo.SelectedIndex : DeckRollupActivation.DelayedHover,
            (int)Math.Round(HoverDelaySlider.Value),
            LockedCheck.IsChecked == true,
            TitlebarBottomNearTopCheck.IsChecked == true,
            string.IsNullOrWhiteSpace(BackgroundColorBox.Text) ? "#17191E" : BackgroundColorBox.Text.Trim(),
            Math.Clamp(1d - (BackgroundTransparencySlider.Value / 100d), 0d, 1d));
    }

    private bool ApplyChanges()
    {
        SaveCurrentGroupDraft();
        // Stage edits, including both feature switches, rather than writing
        // through to the running configuration before the save checkpoint.
        var liveSettings = _state.Settings;
        var beforeSettings = liveSettings.Clone();
        var s = liveSettings.Clone();
        s.EnableCyanDeck = DeckModuleCheck.IsChecked == true;
        s.EnableCalendar = CalendarModuleCheck.IsChecked == true;
        s.GoogleCalendarClientId = GoogleClientIdBox.Text.Trim();
        if (double.TryParse(CalendarWidthBox.Text, out var calendarWidth) && double.IsFinite(calendarWidth))
            s.CalendarWidth = Math.Max(calendarWidth, 390);
        if (double.TryParse(CalendarHeightBox.Text, out var calendarHeight) && double.IsFinite(calendarHeight))
            s.CalendarHeight = Math.Clamp(calendarHeight, 310, 1200);
        s.CalendarViewMode = (CalendarViewModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Rolling";
        s.CalendarVisibleWeekRows = CalendarRowsCombo.SelectedItem is int calendarRows ? calendarRows : 6;
        s.CalendarChameleonEnabled = CalendarChameleonCheck.IsChecked == true;
        if (System.Text.RegularExpressions.Regex.IsMatch(CalendarBackgroundColorBox.Text.Trim(), "^#[0-9A-Fa-f]{6}$"))
            s.CalendarBackgroundColor = CalendarBackgroundColorBox.Text.Trim().ToUpperInvariant();
        s.CalendarIdleOpacity = Math.Clamp(CalendarIdleOpacitySlider.Value, 0.15, 1);
        s.CalendarActiveBackgroundOpacity = Math.Clamp(CalendarActiveOpacitySlider.Value, 0.15, 1);
        s.CalendarDateFontSize = Math.Clamp(CalendarDateFontSizeSlider.Value, 9, 24);
        s.CalendarEventFontSize = Math.Clamp(CalendarEventFontSizeSlider.Value, 9, 24);
        s.HideNativeDesktopIcons = HideNativeIconsCheck.IsChecked == true;
        s.ShowStandaloneRecycleBin = StandaloneRecycleCheck.IsChecked == true;
        s.ShowRecycleBinInSystemGroup = GroupRecycleCheck.IsChecked == true;
        s.AutoImportDesktop = AutoImportCheck.IsChecked == true;
        s.LeaveNewDesktopItemsUngrouped = LeaveNewDesktopItemsCheck.IsChecked == true;
        ApplyDefaultDesktopPlacementSelection(s);
        s.ShowFilePreviews = ShowFilePreviewsCheck.IsChecked == true;
        s.HideFileShortcutBadges = HideFileShortcutBadgesCheck.IsChecked == true;
        s.HideInternetShortcutBadges = HideInternetShortcutBadgesCheck.IsChecked == true;
        s.ShowShortcutBadgesInFolderPortals = ShowShortcutBadgesInFolderPortalsCheck.IsChecked == true;
        s.KeepRulesUpdatedAtAllTimes = KeepRulesUpdatedCheck.IsChecked == true;
        s.TrackDesktopLocationChanges = TrackDesktopLocationCheck.IsChecked == true;
        s.StartWithWindows = StartupCheck.IsChecked == true;
        s.EnableEdgePageGestures = EdgeGesturesCheck.IsChecked == true;
        s.AllowInteriorMonitorSeamPageNavigation = InteriorSeamGesturesCheck.IsChecked == true;
        s.EnableTouchPageGestures = TouchPageGesturesCheck.IsChecked == true;
        s.EnableTouchIconFlick = TouchIconFlickCheck.IsChecked == true;
        s.EnablePageAltMouseWheel = PageAltWheelCheck.IsChecked == true;
        s.EnablePageMiddleDrag = PageMiddleDragCheck.IsChecked == true;
        s.EnablePageBothButtonsDrag = PageBothButtonsDragCheck.IsChecked == true;
        s.CreateGroupMouseButtonMode = CreateGroupMouseButtonCombo.SelectedIndex switch
        {
            0 => DeckCreationMouseButtonMode.LeftOnly,
            2 => DeckCreationMouseButtonMode.Either,
            _ => DeckCreationMouseButtonMode.RightOnly
        };
        s.UnhideWhenSwitchingPages = UnhideOnPageSwitchCheck.IsChecked == true;
        s.AnimatePageTransitions = AnimatePagesCheck.IsChecked == true;
        s.EnhancedDesktopPageEasing = EnhancedPageEasingCheck.IsChecked == true;
        s.DesktopPageEasingProfile = PageEasingProfileCombo.SelectedIndex switch
        {
            1 => DesktopPageEasingProfile.Snappy,
            2 => DesktopPageEasingProfile.Gentle,
            _ => DesktopPageEasingProfile.Smooth
        };
        s.BouncePageTransitions = BouncePagesCheck.IsChecked == true;
        s.EnableSmartGroupSnapping = SmartSnapCheck.IsChecked == true;
        s.UseSameSpacingInsideAndOutsideGroups = LinkedIconSpacingCheck.IsChecked == true;
        s.SnapGroupSizeToIconMultiples = SnapGroupSizeCheck.IsChecked == true;
        s.GroupSnapGap = (int)Math.Round(SnapGapSlider.Value);
        s.EnableTabGroups = EnableTabGroupsCheck.IsChecked == true;
        s.ConfirmTabGroupMerge = ConfirmTabGroupMergeCheck.IsChecked == true;
        s.SwitchTabsOnHover = TabSwitchModeCombo.SelectedIndex == 1;
        s.EnableRollup = EnableRollupCheck.IsChecked == true;
        s.EnableRollupDoubleClick = RollupDoubleClickCheck.IsChecked == true;
        s.EnableRollupEdgeDock = RollupEdgeDockCheck.IsChecked == true;
        if (!s.EnableRollup)
        {
            foreach (var fence in _state.Fences)
            {
                fence.RolledUp = false;
                fence.RollupDockEdge = DeckRollupDockEdge.None;
            }
        }
        s.PageTransitionDurationMs = (int)Math.Round(PageDurationSlider.Value);
        s.EnableChameleon = ChameleonCheck.IsChecked == true;
        s.ChameleonFullColorOnMouseover = ChameleonFullColorHoverCheck.IsChecked == true;
        s.ChameleonFullColorWhenDesktopInUse = ChameleonDesktopUseCheck.IsChecked == true;
        s.BlurWallpaperBehindGroups = BlurWallpaperCheck.IsChecked == true;
        s.ChameleonIdleOpacity = IdleOpacitySlider.Value;
        s.StandaloneIconChameleonEnabled = StandaloneIconChameleonCheck.IsChecked == true;
        s.StandaloneIconIdleOpacity = Math.Clamp(StandaloneIconIdleOpacitySlider.Value, 0.15, 1d);
        s.StandaloneIconHoverOpacity = Math.Clamp(StandaloneIconHoverOpacitySlider.Value, 0.15, 1d);
        s.ApplyStandaloneIconChameleonToRecycleBin = StandaloneIconIncludeRecycleCheck.IsChecked == true;
        s.MaxLayoutSnapshots = (int)Math.Round(SnapshotCountSlider.Value);
        s.EnableAutomaticDailySnapshots = AutomaticSnapshotsCheck.IsChecked == true;
        s.CaptureSnapshotScreenPreviews = SnapshotScreenPreviewCheck.IsChecked == true;
        s.CaptureAutomaticSnapshotScreenPreviews = AutomaticSnapshotScreenPreviewCheck.IsChecked == true;
        s.AutomaticSnapshotRetentionDays = (int)Math.Round(AutoSnapshotDaysSlider.Value);
        s.LayoutAdjustmentMode = LayoutAdjustmentCombo.SelectedIndex switch
        {
            0 => DeckLayoutAdjustmentMode.None,
            1 => DeckLayoutAdjustmentMode.Scale,
            _ => DeckLayoutAdjustmentMode.PerMonitor
        };
        s.PeekHotCornerEnabled = PeekHotCornerCheck.IsChecked == true;
        s.PeekHotCornerLeftAction = Enum.IsDefined(typeof(PeekHotCornerAction), PeekHotCornerLeftCombo.SelectedIndex) ? (PeekHotCornerAction)PeekHotCornerLeftCombo.SelectedIndex : PeekHotCornerAction.ShowFences;
        s.PeekHotCornerRightAction = Enum.IsDefined(typeof(PeekHotCornerAction), PeekHotCornerRightCombo.SelectedIndex) ? (PeekHotCornerAction)PeekHotCornerRightCombo.SelectedIndex : PeekHotCornerAction.ShowFullDesktop;
        s.PeekTaskbarButtonEnabled = PeekTaskbarButtonCheck.IsChecked == true;
        s.PeekRespectWorkingArea = PeekWorkAreaCheck.IsChecked == true;
        s.PersistDebounceMs = (int)Math.Round(SaveDebounceSlider.Value);
        s.ProtectMultiSelectionDuringDrag = ProtectMultiSelectionCheck.IsChecked == true;
        s.ShowControlsInGroupHeaders = ShowHeaderControlsCheck.IsChecked == true;
        s.ShowTitlebarControlsOnMouseover = ShowTitlebarControlsHoverCheck.IsChecked == true;
        s.ShowTitlebarControlsInInactiveTabs = ShowInactiveTabControlsCheck.IsChecked == true;
        s.HideScrollbarsWhenInactive = HideInactiveScrollbarsCheck.IsChecked == true;
        s.AnimateItemChanges = AnimateItemChangesCheck.IsChecked == true;
        s.AnimateIconsWhenMoved = AnimateIconsWhenMovedCheck.IsChecked == true;
        s.IconMovementAnimationDurationMs = Math.Clamp((int)Math.Round(IconMovementDurationSlider.Value), IconMovementAnimationOptions.MinimumDurationMs, IconMovementAnimationOptions.MaximumDurationMs);
        s.IconMovementAnimationMaxItems = Math.Clamp((int)Math.Round(IconMovementMaxItemsSlider.Value), IconMovementAnimationOptions.MinimumBatchSize, IconMovementAnimationOptions.MaximumBatchSize);
        s.ConfirmLargeBatchOperations = ConfirmLargeBatchCheck.IsChecked == true;
        s.LargeBatchOperationThreshold = (int)Math.Round(BatchThresholdSlider.Value);
        s.PortalHistoryLimit = (int)Math.Round(PortalHistorySlider.Value);
        s.EnableAsyncItemLoading = AsyncItemLoadingCheck.IsChecked == true;
        s.AsyncItemLoadThreshold = (int)Math.Round(AsyncThresholdSlider.Value);
        s.IconCacheMaxEntries = (int)Math.Round(IconCacheSlider.Value);
        s.DeferHiddenPageRendering = DeferHiddenPageRenderingCheck.IsChecked == true;
        s.EnableQuickHide = QuickHideCheck.IsChecked == true;
        s.QuickHideAlwaysShowIconsAtStartup = QuickHideStartupShowCheck.IsChecked == true;
        s.QuickHideMode = QuickHideModeCombo.SelectedIndex switch
        {
            1 => DeckQuickHideMode.UngroupedIconsOnly,
            2 => DeckQuickHideMode.GroupsOnly,
            _ => DeckQuickHideMode.AllDesktop
        };
        s.EnableCrashLoopProtection = CrashLoopProtectionCheck.IsChecked == true;
        s.CrashLoopThreshold = (int)Math.Round(CrashLoopThresholdSlider.Value);
        s.CrashLoopWindowMinutes = (int)Math.Round(CrashLoopWindowSlider.Value);
        s.EnableAutomaticUpdateChecks = AutomaticUpdateCheck.IsChecked == true;
        s.InstallUpdatesAutomatically = AutomaticUpdateCheck.IsChecked == true && InstallUpdatesAutomaticallyCheck.IsChecked == true;
        s.EnableBetaUpdateChannel = BetaUpdateChannelCheck.IsChecked == true;
        s.UpdateManifestSource = UpdateManifestSourceBox.Text.Trim();
        s.RequireSignedUpdateManifests = true;
        s.UpdatePublicKeyPath = UpdatePublicKeyPathBox.Text.Trim();

        // The fence objects are bound to running windows, so preserve their
        // presentation values for rollback without replacing object identities.
        var previousFences = _state.Fences.Select(f => (Fence: f, Draft: GroupDraft.From(f))).ToArray();
        foreach (var fence in _state.Fences)
            if (_drafts.TryGetValue(fence.Id, out var d)) ApplyGroupDraft(fence, d);

        liveSettings.CopyFrom(s);
        // The controller's first action is now a synchronous persisted checkpoint.
        // A false result means nothing has been committed or reconfigured.
        bool applied;
        try { applied = _apply(); }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Settings apply failed", ex);
            MessageBox.Show(this,
                $"설정 저장 이후 실행 상태 적용에 실패했을 수 있습니다. 값은 임의로 되돌리지 않았습니다. 진단 로그를 확인하고 프로그램을 다시 시작하세요.\n\n{ex.Message}",
                "CyanDeck 설정", MessageBoxButton.OK, MessageBoxImage.Error);
            // A runtime failure after an already successful commit cannot be
            // rolled back safely here; report it instead of claiming a rollback.
            return false;
        }
        if (!applied)
        {
            liveSettings.CopyFrom(beforeSettings);
            foreach (var (fence, draft) in previousFences) ApplyGroupDraft(fence, draft);
            QuickHideCheck.IsChecked = beforeSettings.EnableQuickHide;
            QuickHideStartupShowCheck.IsChecked = beforeSettings.QuickHideAlwaysShowIconsAtStartup;
        }
        return applied;
    }

    private static void ApplyGroupDraft(DeckFence fence, GroupDraft d)
    {
        fence.IconLayoutMode = d.IconLayoutMode;
        fence.IconLabelMode = d.IconLabelMode;
        fence.TitlebarMode = d.TitlebarMode;
        fence.TitleFontFamily = d.TitleFontFamily;
        fence.TitleFontSize = d.TitleFontSize;
        fence.IconLabelFontSize = Math.Clamp(d.IconLabelFontSize, 9, 24);
        fence.TitleForeground = d.TitleForeground;
        fence.IconSize = d.IconSize;
        fence.IconSpacingX = d.IconSpacingX;
        fence.IconSpacingY = d.IconSpacingY;
        fence.Background = d.Background;
        fence.Opacity = d.Opacity;
        fence.RollupActivation = d.RollupActivation;
        fence.RollupHoverDelayMs = d.RollupHoverDelayMs;
        fence.Locked = d.Locked;
        fence.MoveTitlebarToBottomNearTop = d.MoveTitlebarToBottomNearTop;
    }

    private void AddSystemIconGroup_Click(object sender, RoutedEventArgs e)
    {
        // Save checkbox changes before creating the group so its contents follow the
        // new preference. Never make an uncommitted settings draft externally visible.
        if (!ApplyChanges()) return;
        _addSystemIconGroup();
        BuildDefaultDesktopPlacementChoices(_state.Settings);
    }

    private void ResetStandaloneRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyChanges()) return;
        _resetStandaloneRecycleBinPosition();
    }

    // Navigate within a small number of existing editor views. We preserve the controls,
    // their binding/draft state and the existing Apply/OK/Cancel transaction boundary.
    private void SettingsNavigation_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem selected || selected.Tag is not string route) return;
        var parts = route.Split('|', 2);
        if (parts.Length != 2) return;
        var tab = parts[0] switch
        {
            "GeneralTab" => GeneralTab,
            "InteractionTab" => InteractionTab,
            "AppearanceTab" => AppearanceTab,
            "AnimationTab" => AnimationTab,
            "PerformanceTab" => PerformanceTab,
            "LayoutTab" => LayoutTab,
            "CalendarTab" => CalendarTab,
            "SystemIconsTab" => SystemIconsTab,
            "GroupsTab" => GroupsTab,
            "AboutTab" => AboutTab,
            _ => null
        };
        if (tab is null || !tab.IsEnabled) return;
        SettingsTabs.SelectedItem = tab;
        // After the selected TabItem is measured, BringIntoView scrolls its ScrollViewer.
        var anchorName = parts[1];
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (SettingsTabs.SelectedItem != tab) return;
            if (FindName(anchorName) is FrameworkElement anchor)
                anchor.BringIntoView(new Rect(0, 0, Math.Max(1, anchor.ActualWidth), Math.Max(1, anchor.ActualHeight)));
        }));
    }

    private static IEnumerable<TreeViewItem> SettingsNavigationLeaves(ItemsControl parent)
    {
        foreach (var raw in parent.Items)
        {
            if (raw is not TreeViewItem item) continue;
            if (item.HasItems)
            {
                foreach (var child in SettingsNavigationLeaves(item)) yield return child;
            }
            else yield return item;
        }
    }

    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SettingsSearchHint is null || SettingsTabs is null || SettingsNavigation is null) return;
        var query = SettingsSearchBox.Text.Trim();
        var leaves = SettingsNavigationLeaves(SettingsNavigation).ToList();
        if (query.Length == 0)
        {
            foreach (var leaf in leaves) leaf.Visibility = Visibility.Visible;
            foreach (var root in SettingsNavigation.Items.OfType<TreeViewItem>()) root.Visibility = Visibility.Visible;
            SettingsSearchHint.Text = "";
            return;
        }

        // Search the section labels, and a few common terms users type instead of labels.
        // Unlike the previous coarse tab-only search, results link directly to a section.
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DesktopSection"] = "바탕화면 자동 분류 시작 프로그램 onedrive 새 항목 가져오기",
            ["DesktopIconStyleSection"] = "아이콘 썸네일 미리보기 배지 바로가기 화살표 파일 폴더 모양 외관",
            ["GroupChromeSection"] = "그룹 헤더 제목줄 제목 표시 버튼 스크롤바 비활성",
            ["PageAnimationSection"] = "애니메이션 페이지 전환 이징 시간 부드럽게 바운스",
            ["ItemAnimationSection"] = "애니메이션 아이콘 이동 효과 항목 배치 시간 최대 항목 수",
            ["LoadingPerformanceSection"] = "성능 대량 작업 확인 폴더 포털 로딩 캐시",
            ["QuickHideSection"] = "빠른 숨김 아이콘 그룹 표시 숨김",
            ["BackupSection"] = "백업 내보내기 가져오기 복원",
            ["TabsSection"] = "탭 전환 클릭 포인터 호버 그룹 접기 마우스",
            ["GesturesSection"] = "제스처 페이지 마우스 터치 휠 드래그 스와이프",
            ["PeekSection"] = "peek 핫코너 빠른 숨김 quick hide",
            ["PerformanceSection"] = "성능 저장 지연 Peek 화면 작업영역 복구",
            ["ChameleonSection"] = "흐리게 투명도 불투명도 색상 블러 카멜레온",
            ["SnapshotsSection"] = "레이아웃 백업 모니터 스냅샷 자동 조정",
            ["ResetSection"] = "초기화 복구 백업 가져오기 내보내기",
            ["FactoryResetSection"] = "프로그램 초기화 모든 창 위치 크기 배치 그룹 복원 삭제된 그룹 빠진 아이콘 캘린더 초기 설정 전체 프로그램 공장 초기화 백업 개인정보 데이터 삭제 완전 초기화 계정 로그아웃 로컬 일정",
            ["GoogleSection"] = "구글 google oauth 연결 로그인 계정 클라이언트",
            ["CalendarSection"] = "달력 캘린더 calendar 배경색 투명도 크기 날짜",
            ["StandaloneSection"] = "개별 독립 아이콘 카멜레온 불투명도",
            ["GroupEditorSection"] = "그룹 아이콘 이름 글자 글꼴 폰트 크기 배치 제목 크기 색상 글꼴 배경",
            ["RecycleSection"] = "휴지통 recycle bin 표시 숨기기",
            ["SystemGroupSection"] = "윈도우 windows pc 제어판 네트워크 기본 아이콘 그룹",
            ["TroubleshootSection"] = "문제 해결 진단 로그 복구 안전 모드",
            ["UpdatesSection"] = "버전 업데이트 배포 릴리스"
        };
        var count = 0;
        TreeViewItem? first = null;
        foreach (var leaf in leaves)
        {
            var route = leaf.Tag as string ?? "";
            var anchor = route.Split('|').LastOrDefault() ?? "";
            var haystack = $"{leaf.Header} {aliases.GetValueOrDefault(anchor, "")}";
            var match = haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
            leaf.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (!match) continue;
            count++;
            first ??= leaf;
        }
        foreach (var root in SettingsNavigation.Items.OfType<TreeViewItem>())
        {
            root.Visibility = root.Items.OfType<TreeViewItem>().Any(child => child.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
            if (root.Visibility == Visibility.Visible) root.IsExpanded = true;
        }
        SettingsSearchHint.Text = count == 0 ? "일치하는 설정 없음" : $"{count}개 분류";
        if (first is not null) first.IsSelected = true;
    }

    private void CompactPreset_Click(object sender, RoutedEventArgs e) => ApplyGroupPreset(34, 6, 6, 10);
    private void BalancedPreset_Click(object sender, RoutedEventArgs e) => ApplyGroupPreset(46, 14, 12, 12);
    private void LargePreset_Click(object sender, RoutedEventArgs e) => ApplyGroupPreset(72, 22, 20, 14);

    private void ApplyGroupPreset(double iconSize, double spacingX, double spacingY, double titleSize)
    {
        if (_currentFence is null) return;
        IconSizeSlider.Value = iconSize;
        SpacingXSlider.Value = spacingX;
        SpacingYSlider.Value = spacingY;
        TitleFontSizeSlider.Value = titleSize;
        SaveCurrentGroupDraft();
        UpdateLabels();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Info("Settings apply button clicked");
        ApplyChanges();
    }
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Info("Settings OK button clicked");
        if (ApplyChanges()) { DialogResult = true; Close(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Info("Settings cancel button clicked");
        Close();
    }
    private void Export_Click(object sender, RoutedEventArgs e) { if (ApplyChanges()) _export(); }
    private void Import_Click(object sender, RoutedEventArgs e) { if (_import()) Close(); }
    private void BuildDefaultDesktopPlacementChoices(DeckSettings settings)
    {
        var choices = new List<DefaultPlacementChoice>
        {
            new("자동 분류 규칙 / 파일 종류 사용", false, DefaultDesktopPlacementKind.Desktop),
            new("그룹 밖 바탕화면", true, DefaultDesktopPlacementKind.Desktop),
        };
        foreach (var fence in _state.Fences)
        {
            foreach (var tab in fence.Tabs.Where(t => t.Kind == DeckTabKind.Collection))
            {
                choices.Add(new DefaultPlacementChoice(
                    $"{fence.Title} / {tab.Title}",
                    true,
                    DefaultDesktopPlacementKind.CollectionTab,
                    fence.Id.ToString("D"),
                    tab.Id.ToString("D")));
            }
        }
        DefaultDesktopPlacementCombo.ItemsSource = choices;
        DefaultDesktopPlacementCombo.DisplayMemberPath = nameof(DefaultPlacementChoice.Label);
        SelectDefaultDesktopPlacement(settings);
    }

    private void SelectDefaultDesktopPlacement(DeckSettings settings)
    {
        if (DefaultDesktopPlacementCombo.ItemsSource is not IEnumerable<DefaultPlacementChoice> source) return;
        var choices = source.ToArray();
        DefaultPlacementChoice? selected;
        if (!settings.UseDefaultDesktopPlacement)
        {
            selected = choices.FirstOrDefault(c => !c.UsesExplicitDefault);
        }
        else if (settings.DefaultDesktopPlacementKind == DefaultDesktopPlacementKind.CollectionTab)
        {
            selected = choices.FirstOrDefault(c => c.UsesExplicitDefault
                && c.Kind == DefaultDesktopPlacementKind.CollectionTab
                && string.Equals(c.GroupId, settings.DefaultDesktopPlacementGroupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.TabId, settings.DefaultDesktopPlacementTabId, StringComparison.OrdinalIgnoreCase));
            selected ??= choices.FirstOrDefault(c => c.UsesExplicitDefault && c.Kind == DefaultDesktopPlacementKind.Desktop);
        }
        else
        {
            selected = choices.FirstOrDefault(c => c.UsesExplicitDefault && c.Kind == DefaultDesktopPlacementKind.Desktop);
        }
        DefaultDesktopPlacementCombo.SelectedItem = selected ?? choices.FirstOrDefault();
        ActivateDefaultDestinationTabCheck.IsChecked = settings.ActivateDefaultDestinationTab;
        LeaveNewDesktopItemsCheck.IsChecked = selected is { UsesExplicitDefault: true, Kind: DefaultDesktopPlacementKind.Desktop };
        UpdateDefaultDesktopPlacementControlState();
    }

    private void ApplyDefaultDesktopPlacementSelection(DeckSettings settings)
    {
        if (DefaultDesktopPlacementCombo.SelectedItem is not DefaultPlacementChoice selected)
        {
            settings.UseDefaultDesktopPlacement = false;
            settings.DefaultDesktopPlacementKind = DefaultDesktopPlacementKind.Desktop;
            settings.DefaultDesktopPlacementGroupId = null;
            settings.DefaultDesktopPlacementTabId = null;
            settings.ActivateDefaultDestinationTab = false;
            settings.LeaveNewDesktopItemsUngrouped = false;
            return;
        }

        settings.UseDefaultDesktopPlacement = selected.UsesExplicitDefault;
        settings.DefaultDesktopPlacementKind = selected.Kind;
        settings.DefaultDesktopPlacementGroupId = selected.Kind == DefaultDesktopPlacementKind.CollectionTab ? selected.GroupId : null;
        settings.DefaultDesktopPlacementTabId = selected.Kind == DefaultDesktopPlacementKind.CollectionTab ? selected.TabId : null;
        settings.ActivateDefaultDestinationTab = selected.UsesExplicitDefault
            && selected.Kind == DefaultDesktopPlacementKind.CollectionTab
            && ActivateDefaultDestinationTabCheck.IsChecked == true;
        settings.LeaveNewDesktopItemsUngrouped = selected.UsesExplicitDefault
            && selected.Kind == DefaultDesktopPlacementKind.Desktop;
        LeaveNewDesktopItemsCheck.IsChecked = settings.LeaveNewDesktopItemsUngrouped;
    }

    private void DefaultDesktopPlacementCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDefaultDesktopPlacementControlState();

    private void UpdateDefaultDesktopPlacementControlState()
    {
        if (ActivateDefaultDestinationTabCheck is null || DefaultDesktopPlacementCombo is null) return;
        var selectedCollection = DefaultDesktopPlacementCombo.SelectedItem is DefaultPlacementChoice
        {
            UsesExplicitDefault: true,
            Kind: DefaultDesktopPlacementKind.CollectionTab
        };
        var locked = _isManagedSettingLocked?.Invoke("Automation.ActivateDefaultDestinationTab") == true;
        ActivateDefaultDestinationTabCheck.IsEnabled = selectedCollection && !locked;
    }

    private void IconMovementDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IconMovementDurationText is not null) IconMovementDurationText.Text = $"{Math.Round(IconMovementDurationSlider.Value):0} ms";
    }

    private void IconMovementMaxItemsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IconMovementMaxItemsText is not null) IconMovementMaxItemsText.Text = $"{Math.Round(IconMovementMaxItemsSlider.Value):0}개";
    }

    private void ResetMonitorLayouts_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "저장된 모니터 레이아웃 기록을 지우시겠습니까? 현재 그룹 위치는 유지되며, 모니터별/비율 조정 모드에서는 현재 배치가 새 기준이 됩니다.", "CyanDeck", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _resetMonitorLayouts();
    }

    private void AutomaticLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyChanges()) return;
        Close();
        _automaticLayout();
    }

    private void SwapScreenContents_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyChanges()) return;
        _swapScreenContents();
    }

    private void ResetGeneral_Click(object sender, RoutedEventArgs e)
    {
        var d = new DeckSettings();
        DeckModuleCheck.IsChecked = d.EnableCyanDeck;
        CalendarModuleCheck.IsChecked = d.EnableCalendar;
        GoogleClientIdBox.Text = d.GoogleCalendarClientId;
        CalendarWidthBox.Text = d.CalendarWidth.ToString("F0");
        CalendarHeightBox.Text = d.CalendarHeight.ToString("F0");
        CalendarViewModeCombo.SelectedIndex = 0;
        CalendarRowsCombo.SelectedItem = d.CalendarVisibleWeekRows;
        CalendarChameleonCheck.IsChecked = d.CalendarChameleonEnabled;
        CalendarBackgroundColorBox.Text = d.CalendarBackgroundColor;
        CalendarIdleOpacitySlider.Value = d.CalendarIdleOpacity;
        CalendarActiveOpacitySlider.Value = d.CalendarActiveBackgroundOpacity;
        CalendarDateFontSizeSlider.Value = d.CalendarDateFontSize;
        CalendarEventFontSizeSlider.Value = d.CalendarEventFontSize;
        HideNativeIconsCheck.IsChecked = d.HideNativeDesktopIcons;
        StandaloneRecycleCheck.IsChecked = d.ShowStandaloneRecycleBin;
        GroupRecycleCheck.IsChecked = d.ShowRecycleBinInSystemGroup;
        AutoImportCheck.IsChecked = d.AutoImportDesktop;
        LeaveNewDesktopItemsCheck.IsChecked = d.LeaveNewDesktopItemsUngrouped;
        SelectDefaultDesktopPlacement(d);
        ShowFilePreviewsCheck.IsChecked = d.ShowFilePreviews;
        HideFileShortcutBadgesCheck.IsChecked = d.HideFileShortcutBadges;
        HideInternetShortcutBadgesCheck.IsChecked = d.HideInternetShortcutBadges;
        ShowShortcutBadgesInFolderPortalsCheck.IsChecked = d.ShowShortcutBadgesInFolderPortals;
        KeepRulesUpdatedCheck.IsChecked = d.KeepRulesUpdatedAtAllTimes;
        TrackDesktopLocationCheck.IsChecked = d.TrackDesktopLocationChanges;
        StartupCheck.IsChecked = d.StartWithWindows;
        EdgeGesturesCheck.IsChecked = d.EnableEdgePageGestures;
        InteriorSeamGesturesCheck.IsChecked = d.AllowInteriorMonitorSeamPageNavigation;
        TouchPageGesturesCheck.IsChecked = d.EnableTouchPageGestures;
        TouchIconFlickCheck.IsChecked = d.EnableTouchIconFlick;
        PageAltWheelCheck.IsChecked = d.EnablePageAltMouseWheel;
        PageMiddleDragCheck.IsChecked = d.EnablePageMiddleDrag;
        PageBothButtonsDragCheck.IsChecked = d.EnablePageBothButtonsDrag;
        CreateGroupMouseButtonCombo.SelectedIndex = d.CreateGroupMouseButtonMode switch
        {
            DeckCreationMouseButtonMode.LeftOnly => 0,
            DeckCreationMouseButtonMode.Either => 2,
            _ => 1
        };
        LinkedIconSpacingCheck.IsChecked = d.UseSameSpacingInsideAndOutsideGroups;
        UnhideOnPageSwitchCheck.IsChecked = d.UnhideWhenSwitchingPages;
        AnimatePagesCheck.IsChecked = d.AnimatePageTransitions;
        EnhancedPageEasingCheck.IsChecked = d.EnhancedDesktopPageEasing;
        PageEasingProfileCombo.SelectedIndex = d.DesktopPageEasingProfile switch
        {
            DesktopPageEasingProfile.Snappy => 1,
            DesktopPageEasingProfile.Gentle => 2,
            _ => 0
        };
        BouncePagesCheck.IsChecked = d.BouncePageTransitions;
        SmartSnapCheck.IsChecked = d.EnableSmartGroupSnapping;
        SnapGroupSizeCheck.IsChecked = d.SnapGroupSizeToIconMultiples;
        SnapGapSlider.Value = d.GroupSnapGap;
        EnableTabGroupsCheck.IsChecked = d.EnableTabGroups;
        ConfirmTabGroupMergeCheck.IsChecked = d.ConfirmTabGroupMerge;
        TabSwitchModeCombo.SelectedIndex = d.SwitchTabsOnHover ? 1 : 0;
        EnableRollupCheck.IsChecked = d.EnableRollup;
        RollupDoubleClickCheck.IsChecked = d.EnableRollupDoubleClick;
        RollupEdgeDockCheck.IsChecked = d.EnableRollupEdgeDock;
        PageDurationSlider.Value = d.PageTransitionDurationMs;
        ChameleonCheck.IsChecked = d.EnableChameleon;
        ChameleonFullColorHoverCheck.IsChecked = d.ChameleonFullColorOnMouseover;
        ChameleonDesktopUseCheck.IsChecked = d.ChameleonFullColorWhenDesktopInUse;
        BlurWallpaperCheck.IsChecked = d.BlurWallpaperBehindGroups;
        IdleOpacitySlider.Value = d.ChameleonIdleOpacity;
        StandaloneIconChameleonCheck.IsChecked = d.StandaloneIconChameleonEnabled;
        StandaloneIconIdleOpacitySlider.Value = d.StandaloneIconIdleOpacity;
        StandaloneIconHoverOpacitySlider.Value = d.StandaloneIconHoverOpacity;
        StandaloneIconIncludeRecycleCheck.IsChecked = d.ApplyStandaloneIconChameleonToRecycleBin;
        SnapshotCountSlider.Value = d.MaxLayoutSnapshots;
        AutomaticSnapshotsCheck.IsChecked = d.EnableAutomaticDailySnapshots;
        SnapshotScreenPreviewCheck.IsChecked = d.CaptureSnapshotScreenPreviews;
        AutomaticSnapshotScreenPreviewCheck.IsChecked = d.CaptureAutomaticSnapshotScreenPreviews;
        AutoSnapshotDaysSlider.Value = d.AutomaticSnapshotRetentionDays;
        LayoutAdjustmentCombo.SelectedIndex = (int)d.LayoutAdjustmentMode;
        PeekWorkAreaCheck.IsChecked = d.PeekRespectWorkingArea;
        SaveDebounceSlider.Value = d.PersistDebounceMs;
        ProtectMultiSelectionCheck.IsChecked = d.ProtectMultiSelectionDuringDrag;
        ShowHeaderControlsCheck.IsChecked = d.ShowControlsInGroupHeaders;
        ShowTitlebarControlsHoverCheck.IsChecked = d.ShowTitlebarControlsOnMouseover;
        ShowInactiveTabControlsCheck.IsChecked = d.ShowTitlebarControlsInInactiveTabs;
        HideInactiveScrollbarsCheck.IsChecked = d.HideScrollbarsWhenInactive;
        AnimateItemChangesCheck.IsChecked = d.AnimateItemChanges;
        AnimateIconsWhenMovedCheck.IsChecked = d.AnimateIconsWhenMoved;
        IconMovementDurationSlider.Value = d.IconMovementAnimationDurationMs;
        IconMovementMaxItemsSlider.Value = d.IconMovementAnimationMaxItems;
        ConfirmLargeBatchCheck.IsChecked = d.ConfirmLargeBatchOperations;
        BatchThresholdSlider.Value = d.LargeBatchOperationThreshold;
        PortalHistorySlider.Value = d.PortalHistoryLimit;
        AsyncItemLoadingCheck.IsChecked = d.EnableAsyncItemLoading;
        AsyncThresholdSlider.Value = d.AsyncItemLoadThreshold;
        IconCacheSlider.Value = d.IconCacheMaxEntries;
        DeferHiddenPageRenderingCheck.IsChecked = d.DeferHiddenPageRendering;
        CrashLoopProtectionCheck.IsChecked = d.EnableCrashLoopProtection;
        CrashLoopThresholdSlider.Value = d.CrashLoopThreshold;
        CrashLoopWindowSlider.Value = d.CrashLoopWindowMinutes;
        AutomaticUpdateCheck.IsChecked = d.EnableAutomaticUpdateChecks || d.InstallUpdatesAutomatically;
        InstallUpdatesAutomaticallyCheck.IsChecked = d.InstallUpdatesAutomatically;
        BetaUpdateChannelCheck.IsChecked = d.EnableBetaUpdateChannel;
        UpdateManifestSourceBox.Text = d.UpdateManifestSource;
        RequireSignedUpdateCheck.IsChecked = true;
        UpdatePublicKeyPathBox.Text = d.UpdatePublicKeyPath;
        QuickHideCheck.IsChecked = d.EnableQuickHide;
        QuickHideStartupShowCheck.IsChecked = d.QuickHideAlwaysShowIconsAtStartup;
        QuickHideModeCombo.SelectedIndex = (int)d.QuickHideMode;
        PeekHotCornerCheck.IsChecked = d.PeekHotCornerEnabled;
        PeekHotCornerLeftCombo.SelectedIndex = (int)d.PeekHotCornerLeftAction;
        PeekHotCornerRightCombo.SelectedIndex = (int)d.PeekHotCornerRightAction;
        PeekTaskbarButtonCheck.IsChecked = d.PeekTaskbarButtonEnabled;
        UpdateLabels();
    }

    private void ResetSelectedGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFence is null) return;
        var d = GroupDraft.From(new DeckFence());
        _drafts[_currentFence.Id] = d;
        LoadCurrentGroupDraft();
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => _openSettingsFolder();
    private void OpenDiagnosticLogs_Click(object sender, RoutedEventArgs e) => _openDiagnosticLogs();
    private void OpenSettingsAuditLog_Click(object sender, RoutedEventArgs e) => _openSettingsAuditLog();
    private void RunSelfTest_Click(object sender, RoutedEventArgs e) => _runSelfTest();
    private void CreateDiagnosticBundle_Click(object sender, RoutedEventArgs e) => _createDiagnosticBundle();
    private void AboutValidateConfiguration_Click(object sender, RoutedEventArgs e) => _validateConfiguration();
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyChanges()) return;
        await _checkForUpdates();
        LastUpdateCheckText.Text = _state.Settings.LastUpdateCheckUtc is DateTime lastUpdate ? $"마지막 업데이트 확인: {lastUpdate.ToLocalTime():g}" : "마지막 업데이트 확인: 없음";
        RefreshUpdateDiagnostics();
    }
    private void RefreshUpdateDiagnostics()
    {
        var result = UpdateFailureDiagnostics.Inspect(DataRootPaths.ActiveLocal, DateTime.UtcNow);
        UpdateFailureSummaryText.Text = result.Text +
            (UpdateService.HasPendingRecovery() ? Environment.NewLine + "미완료 업데이트 복구 기록 있음 · 새 설치 전 복구 필요" : "");
        OpenUpdateFailureLogButton.IsEnabled = result.HasLog;
    }

    private void RefreshUpdateDiagnostics_Click(object sender, RoutedEventArgs e)
        => RefreshUpdateDiagnostics();

    private void OpenUpdateFailureLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var summary = UpdateFailureDiagnostics.Inspect(DataRootPaths.ActiveLocal, DateTime.UtcNow);
            if (!summary.HasLog) { RefreshUpdateDiagnostics(); return; }
            var path = UpdateFailureDiagnostics.LogPath(DataRootPaths.ActiveLocal);
            var process = new System.Diagnostics.ProcessStartInfo("notepad.exe",
                "\"" + path + "\"") { UseShellExecute = true };
            System.Diagnostics.Process.Start(process);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "업데이트 오류 로그를 열지 못했습니다." + Environment.NewLine + ex.Message,
                "CyanDeck 업데이트 진단", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RollbackUpdate_Click(object sender, RoutedEventArgs e) => _rollbackLastUpdate();
    private void ResetStartupHealth_Click(object sender, RoutedEventArgs e)
    {
        StartupHealthService.Reset();
        StartupHealthText.Text = "최근 비정상 시작이 감지되지 않았습니다.";
        MessageBox.Show(this, "시작 충돌 반복 기록을 초기화했습니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void ResetAllWindowGeometry_Click(object sender, RoutedEventArgs e)
    {
        if (_safeMode || _compatibilityReadOnly || !ResetAllWindowGeometryButton.IsEnabled) return;
        if (_resetAllWindowGeometry() && IsVisible) Close();
    }

    private void ResetPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (_safeMode || _compatibilityReadOnly) return;
        var groups = ResetGroupGeometryCheck.IsEnabled && ResetGroupGeometryCheck.IsChecked == true;
        var calendar = ResetCalendarGeometryCheck.IsEnabled && ResetCalendarGeometryCheck.IsChecked == true;
        if (!groups && !calendar)
        {
            MessageBox.Show(this, "초기화할 항목을 하나 이상 선택하세요.",
                "CyanDeck 위치 초기화", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_resetPlacement(groups, calendar) && IsVisible) Close();
    }

    private void FactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (_compatibilityReadOnly || _safeMode || !_isManagedCapabilityAllowed("ImportExport.Import") ||
            !_isManagedCapabilityAllowed("Settings.EditGeneral")) return;
        if (_factoryReset() && IsVisible) Close();
    }

    private void CompleteLocalReset_Click(object sender, RoutedEventArgs e)
    {
        if (_compatibilityReadOnly || _safeMode || !CompleteLocalResetButton.IsEnabled) return;
        if (_completeLocalReset() && IsVisible) Close();
    }

    private void RestartSafeMode_Click(object sender, RoutedEventArgs e) => _restartSafeMode();
    private void RestartNormal_Click(object sender, RoutedEventArgs e) => _restartNormal();

    private void ApplyRulesNow_Click(object sender, RoutedEventArgs e)
    {
        ApplyChanges();
        _applyRulesNow();
    }

    // Reuse the calendar's exact preset/RGB popover beside each existing HEX editor.
    // Group drafts are still persisted by the Settings Apply action, as before.
    private CyanDeck.Calendar.CalendarColorPicker? _backgroundPicker;
    private CyanDeck.Calendar.CalendarColorPicker? _titlePicker;
    private CyanDeck.Calendar.CalendarColorPicker? _calendarBackgroundPicker;

    private void InitializeColorPickers()
    {
        var editable = !_compatibilityReadOnly;
        _calendarBackgroundPicker = new CyanDeck.Calendar.CalendarColorPicker(
            CalendarBackgroundColorBox.Text, editable,
            _ => { /* Calendar background is committed by ApplyChanges(). */ },
            _ => { }, CalendarBackgroundColorBox, CalendarBackgroundColorSwatch);
        _titlePicker = new CyanDeck.Calendar.CalendarColorPicker(
            _currentFence is null ? "#F4F6FA" : TitleColorBox.Text, editable,
            hex =>
            {
                if (_loading || _currentFence is null || !_drafts.TryGetValue(_currentFence.Id, out var draft)) return;
                _drafts[_currentFence.Id] = draft with { TitleForeground = hex };
            },
            _ => { }, TitleColorBox, TitleColorSwatch);
        _backgroundPicker = new CyanDeck.Calendar.CalendarColorPicker(
            _currentFence is null ? "#17191E" : NormalizeRgbHex(BackgroundColorBox.Text, "#17191E"), editable,
            _ => CommitBackgroundHexEditor(revertOnInvalid: false),
            _ => { }, BackgroundColorBox, BackgroundColorSwatch);
        // The picker accepts RGB. Preserve existing 8-digit alpha input through the
        // original TextBox LostFocus/Enter path and synchronize the preview on valid RGB.
        BackgroundColorBox.TextChanged += (_, _) =>
        {
            if (_loading || !TryParseBackgroundHex(BackgroundColorBox.Text, out _, out var alpha) || alpha) return;
            CommitBackgroundHexEditor(revertOnInvalid: false);
        };
    }

    private void LoadBackgroundEditor(GroupDraft draft)
    {
        if (BackgroundHueSlider is null || BackgroundSaturationSlider is null || BackgroundBrightnessSlider is null || BackgroundTransparencySlider is null || BackgroundColorBox is null)
        {
            return;
        }

        Color color;
        try
        {
            object? parsed = ColorConverter.ConvertFromString(draft.Background);
            color = parsed is Color c ? c : Color.FromRgb(0x17, 0x19, 0x1E);
        }
        catch
        {
            color = Color.FromRgb(0x17, 0x19, 0x1E);
        }

        byte alpha = (byte)Math.Round(Math.Clamp(draft.Opacity, 0d, 1d) * 255d, MidpointRounding.AwayFromZero);
        BackgroundColorAdjustment adjustment = BackgroundColorMath.FromArgb(
            new Argb32(alpha, color.R, color.G, color.B),
            BackgroundHueSlider.Value);
        BackgroundHueSlider.Value = adjustment.HueDegrees;
        BackgroundSaturationSlider.Value = adjustment.SaturationPercent;
        BackgroundBrightnessSlider.Value = adjustment.BrightnessPercent;
        BackgroundTransparencySlider.Value = adjustment.TransparencyPercent;
        BackgroundColorBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _backgroundPicker?.SetDisplayedColor(BackgroundColorBox.Text);
    }

    private void BackgroundColorBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitBackgroundHexEditor(revertOnInvalid: true);
        e.Handled = true;
    }

    private void BackgroundColorBox_LostFocus(object sender, RoutedEventArgs e)
        => CommitBackgroundHexEditor(revertOnInvalid: true);

    private bool CommitBackgroundHexEditor(bool revertOnInvalid)
    {
        if (_loading || _currentFence is null || BackgroundColorBox is null
            || BackgroundHueSlider is null || BackgroundSaturationSlider is null
            || BackgroundBrightnessSlider is null || BackgroundTransparencySlider is null)
        {
            return false;
        }

        if (!TryParseBackgroundHex(BackgroundColorBox.Text, out var color, out var includesAlpha))
        {
            if (revertOnInvalid && _drafts.TryGetValue(_currentFence.Id, out var previous))
            {
                BackgroundColorBox.Text = NormalizeRgbHex(previous.Background, "#17191E");
                BackgroundColorBox.SelectAll();
            }
            return false;
        }

        var rgb = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var opacity = includesAlpha
            ? color.A / 255d
            : Math.Clamp(1d - (BackgroundTransparencySlider.Value / 100d), 0d, 1d);
        var alpha = (byte)Math.Round(opacity * 255d, MidpointRounding.AwayFromZero);
        var adjustment = BackgroundColorMath.FromArgb(
            new Argb32(alpha, color.R, color.G, color.B),
            BackgroundHueSlider.Value);

        var wasLoading = _loading;
        _loading = true;
        try
        {
            BackgroundHueSlider.Value = adjustment.HueDegrees;
            BackgroundSaturationSlider.Value = adjustment.SaturationPercent;
            BackgroundBrightnessSlider.Value = adjustment.BrightnessPercent;
            if (includesAlpha)
            {
                BackgroundTransparencySlider.Value = adjustment.TransparencyPercent;
            }
            BackgroundColorBox.Text = rgb;

            if (_drafts.TryGetValue(_currentFence.Id, out var draft))
            {
                _drafts[_currentFence.Id] = draft with
                {
                    Background = rgb,
                    Opacity = opacity,
                };
            }
        }
        finally
        {
            _loading = wasLoading;
        }

        UpdateLabels();
        return true;
    }

    private static bool TryParseBackgroundHex(string? text, out Color color, out bool includesAlpha)
    {
        color = default;
        includesAlpha = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (value.Length is not (7 or 9))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }

        try
        {
            var parsed = ColorConverter.ConvertFromString(value);
            if (parsed is not Color parsedColor)
            {
                return false;
            }

            color = parsedColor;
            includesAlpha = value.Length == 9;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeRgbHex(string? value, string fallback)
    {
        if (!TryParseBackgroundHex(value, out var color, out _))
        {
            return fallback;
        }
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void BackgroundAdjustment_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (_loading || _currentFence is null || BackgroundHueSlider is null || BackgroundSaturationSlider is null || BackgroundBrightnessSlider is null || BackgroundTransparencySlider is null)
        {
            return;
        }

        Argb32 color = BackgroundColorMath.ToArgb(new BackgroundColorAdjustment(
            BackgroundHueSlider.Value,
            BackgroundSaturationSlider.Value,
            BackgroundBrightnessSlider.Value,
            BackgroundTransparencySlider.Value));
        string rgb = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        BackgroundColorBox.Text = rgb;
        if (_drafts.TryGetValue(_currentFence.Id, out var draft))
        {
            _drafts[_currentFence.Id] = draft with
            {
                Background = rgb,
                Opacity = color.A / 255d,
            };
        }
    }

    private void WallpaperPick_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFence is null)
        {
            return;
        }

        var pickerWindow = new WallpaperEyedropperWindow { Owner = this };
        bool? accepted = pickerWindow.ShowDialog();
        if (accepted != true || pickerWindow.PhysicalScreenPoint is not { } point)
        {
            return;
        }

        var service = new WallpaperColorSampleService(new DesktopWallpaperSnapshotReader(), new WallpaperBitmapSampler());
        var controller = new WallpaperColorPickerController(
            service,
            () => !_compatibilityReadOnly && _currentFence is not null,
            sampled =>
            {
                if (_currentFence is null || !_drafts.TryGetValue(_currentFence.Id, out var draft))
                {
                    return;
                }

                string rgb = $"#{sampled.R:X2}{sampled.G:X2}{sampled.B:X2}";
                _drafts[_currentFence.Id] = draft with { Background = rgb };
                BackgroundColorBox.Text = rgb;
                bool wasLoading = _loading;
                _loading = true;
                try { LoadBackgroundEditor(_drafts[_currentFence.Id]); }
                finally { _loading = wasLoading; }
                UpdateLabels();
            });

        WallpaperPickStatus status = controller.TryApplyAt(point.X, point.Y);
        if (status is WallpaperPickStatus.UnsupportedWallpaper or WallpaperPickStatus.ApplyFailed)
        {
            MessageBox.Show(this,
                "해당 위치의 배경화면 색상을 가져오지 못했습니다. 네트워크 배경화면과 지원하지 않는 이미지 형식은 샘플링하지 않습니다.",
                "CyanDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else if (status == WallpaperPickStatus.Blocked)
        {
            MessageBox.Show(this,
                "호환성 읽기 전용 보호가 활성화된 동안에는 모양 설정을 변경할 수 없습니다.",
                "CyanDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void SnapGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void SaveDebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void BatchThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void PortalHistorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void AsyncThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void IconCacheSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void CrashLoopThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void CrashLoopWindowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void ClearIconCache_Click(object sender, RoutedEventArgs e)
    {
        _clearIconCache();
        MessageBox.Show(this, "메모리의 아이콘/미리보기 캐시를 비웠습니다. 표시 중인 그룹은 필요할 때 다시 채웁니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void ValidateConfiguration_Click(object sender, RoutedEventArgs e) => _validateConfiguration();
    private void PageDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void IdleOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void StandaloneIconOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void SnapshotCountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void AutoSnapshotDaysSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void SpacingXSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void SpacingYSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void HoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void TitleFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    private void IconLabelFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void UpdateLabels()
    {
        if (PageDurationText is null || IdleOpacityText is null || SnapshotCountText is null || AutoSnapshotDaysText is null || SnapGapText is null || SaveDebounceText is null ||
            BatchThresholdText is null || PortalHistoryText is null || AsyncThresholdText is null || IconCacheText is null || CrashLoopThresholdText is null || CrashLoopWindowText is null || IconSizeText is null || SpacingXText is null || SpacingYText is null || HoverDelayText is null || TitleFontSizeText is null ||
            BackgroundHueText is null || BackgroundSaturationText is null || BackgroundBrightnessText is null || BackgroundTransparencyText is null) return;
        SnapGapText.Text = $"{Math.Round(SnapGapSlider.Value):0} px";
        SaveDebounceText.Text = $"{Math.Round(SaveDebounceSlider.Value):0} ms";
        BatchThresholdText.Text = $"{Math.Round(BatchThresholdSlider.Value):0}개";
        PortalHistoryText.Text = $"{Math.Round(PortalHistorySlider.Value):0}개";
        AsyncThresholdText.Text = $"{Math.Round(AsyncThresholdSlider.Value):0}개";
        IconCacheText.Text = $"{Math.Round(IconCacheSlider.Value):0}개";
        CrashLoopThresholdText.Text = $"{Math.Round(CrashLoopThresholdSlider.Value):0}회";
        CrashLoopWindowText.Text = $"{Math.Round(CrashLoopWindowSlider.Value):0}분";
        PageDurationText.Text = $"{Math.Round(PageDurationSlider.Value):0} ms";
        IconMovementDurationText.Text = $"{Math.Round(IconMovementDurationSlider.Value):0} ms";
        IconMovementMaxItemsText.Text = $"{Math.Round(IconMovementMaxItemsSlider.Value):0}개";
        IdleOpacityText.Text = $"{IdleOpacitySlider.Value:P0}";
        if (StandaloneIconIdleOpacityText is not null && StandaloneIconHoverOpacityText is not null)
        {
            StandaloneIconIdleOpacityText.Text = $"{StandaloneIconIdleOpacitySlider.Value:P0}";
            StandaloneIconHoverOpacityText.Text = $"{StandaloneIconHoverOpacitySlider.Value:P0}";
        }
        SnapshotCountText.Text = $"{Math.Round(SnapshotCountSlider.Value):0}";
        AutoSnapshotDaysText.Text = $"{Math.Round(AutoSnapshotDaysSlider.Value):0}일";
        IconSizeText.Text = $"{Math.Round(IconSizeSlider.Value):0} px";
        var linkedSpacingSuffix = LinkedIconSpacingCheck.IsChecked == true ? " (바탕화면)" : "";
        SpacingXText.Text = $"{Math.Round(SpacingXSlider.Value):0} px{linkedSpacingSuffix}";
        SpacingYText.Text = $"{Math.Round(SpacingYSlider.Value):0} px{linkedSpacingSuffix}";
        HoverDelayText.Text = $"{Math.Round(HoverDelaySlider.Value):0} ms";
        TitleFontSizeText.Text = $"{Math.Round(TitleFontSizeSlider.Value):0} px";
        if (IconLabelFontSizeText is not null)
            IconLabelFontSizeText.Text = $"{IconLabelFontSizeSlider.Value:0.#} px";
        BackgroundHueText.Text = $"{Math.Round(BackgroundHueSlider.Value):0}°";
        BackgroundSaturationText.Text = $"{Math.Round(BackgroundSaturationSlider.Value):0}%";
        BackgroundBrightnessText.Text = $"{Math.Round(BackgroundBrightnessSlider.Value):0}%";
        BackgroundTransparencyText.Text = $"{Math.Round(BackgroundTransparencySlider.Value):0}%";
    }

    private sealed record GroupDraft(
        DeckIconLayoutMode IconLayoutMode,
        DeckIconLabelMode IconLabelMode,
        DeckTitlebarMode TitlebarMode,
        string TitleFontFamily,
        double TitleFontSize,
        double IconLabelFontSize,
        string TitleForeground,
        double IconSize,
        double IconSpacingX,
        double IconSpacingY,
        DeckRollupActivation RollupActivation,
        int RollupHoverDelayMs,
        bool Locked,
        bool MoveTitlebarToBottomNearTop,
        string Background,
        double Opacity)
    {
        public static GroupDraft From(DeckFence f) => new(f.IconLayoutMode, f.IconLabelMode, f.TitlebarMode, f.TitleFontFamily, f.TitleFontSize, f.IconLabelFontSize, f.TitleForeground, f.IconSize, f.IconSpacingX, f.IconSpacingY, f.RollupActivation, f.RollupHoverDelayMs, f.Locked, f.MoveTitlebarToBottomNearTop, f.Background, f.Opacity);
    }
}
