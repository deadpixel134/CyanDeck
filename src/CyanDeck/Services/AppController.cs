using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using CyanDeck.Models;
using CyanDeck.Layout;
using CyanDeck.Animations;
using CyanDeck.Input;
using CyanDeck.Interop;
using CyanDeck.Appearance;
using CyanDeck.Automation;
using CyanDeck.Snapshots;
using CyanDeck.Peek;
using CyanDeck.QuickHide;
using CyanDeck.Topology;
using CyanDeck.Display;
using CyanDeck.FolderPortals;
using CyanDeck.ManagedDeployment;
using CyanDeck.Views;
using CyanDeck.Calendar;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

public sealed partial class AppController : IDisposable, ILinkedIconSpacingHost
{
    private readonly Dispatcher _dispatcher;
    private bool _safeMode;
    private readonly StateStore _store = new();
    private readonly DesktopHost _host = new();
    private CalendarModule? _calendar;
    private readonly IconService _icons = new();
    private readonly DesktopDisplayService _desktopDisplay = new();
    private readonly List<FenceWindow> _windows = [];
    private readonly DesktopWatcherLifetime _desktopWatchers = new();
    private DesktopDoubleClickHook? _desktopDoubleClick;
    private DesktopPageGestureHook? _pageGesture;
    private DesktopPageTouchSurfaceHost? _touchPageHost;
    private DesktopFolderPortalDropSurfaceHost? _folderPortalDropHost;
    private DesktopCreationRectangleOverlayHost? _desktopCreationPreviewHost;
    private Forms.ContextMenuStrip? _desktopCreationMenu;
    private LooseDesktopIconHost? _looseDesktopHost;
    private DeckItem? _recycleBinDesktopItem;
    private LooseIconQuickHideExclusionCoordinator? _looseQuickHideCoordinator;
    private QuickHideFeatureController? _quickHideFeatureController;
    private QuickHideStartupVisibilityCoordinator? _quickHideStartupCoordinator;
    private QuickHideStartupApplyStatus? _quickHideStartupStatus;
    private QuickHideShowReason? _quickHideStartupShowReason;
    private bool _quickHideConfiguredEnabled = true;
    private WindowsForegroundEventSource? _foregroundEvents;
    private SnapshotScreenPreviewCoordinator? _snapshotScreenPreviews;
    private WpfPeekHotCornerHost? _peekHotCorner;
    private PeekTaskbarButtonHost? _peekTaskbarButton;
    private TrayIconController? _trayController;
    private bool _trayMenuVisible;
    private Action? _pendingTrayDialog;
    private DesktopShellRecoveryScheduler? _shellRecoveryScheduler;
    private bool _shellRecoveryIdleSuppressed;
    private DispatcherTimer? _displayRestoreTimer;
    // A monitor unplug can rebuild Explorer's FolderView AFTER topology settling.
    // Short, bounded checks repair only its icon visibility; never restack WPF groups.
    private DispatcherTimer? _nativeIconDisplayRepairTimer;
    private int _nativeIconDisplayRepairChecksRemaining;
    private WpfDisplayTransitionBridge? _displayTransition;
    private MonitorContentSwapCoordinator? _monitorContentSwap;
    private bool _monitorContentSwapInProgress;
    private ManagedPolicyCoordinator? _managedPolicy;
    private ManagedPolicyFileWatcher? _managedPolicyWatcher;
    private string? _managedPolicySourcePath;
    private string? _managedPolicyLastError;
    private DateTime? _managedPolicyLastReloadUtc;
    private long _displayRestoreGeneration;
    private int _displayTransitionStableRestoreCount;
    private int _displayTransitionDeferredSaveCount;
    // Do not let an in-flight or shutdown-time snapshot capture stale pre-reset HWNDs.
    private bool _geometryResetPendingRestart;
    private DispatcherTimer? _persistTimer;
    private DesktopReconcileScheduler? _desktopReconcileScheduler;
    private DispatcherTimer? _dailyBackupTimer;
    private DesktopFirstSeenGate? _desktopFirstSeenGate;
    private DeckState _state = new();
    private bool _peek;
    private bool _quickHidden;
    private bool _disposed;
    private bool _automaticUpdateInProgress;
    private bool _restoringMonitorLayout;
    private string _activeMonitorSignature = "";
    // Track the most recent stable topology, independently of persisted profiles.
    // The layout planner runs only when the monitor COUNT actually decreases.
    private int _lastStableMonitorCount;
    private int _pageTransitionToken;
    private DesktopPageTouchSwipeDirection? _touchPagePreviewDirection;
    private double _touchPagePreviewProgress;
    private string? _touchPageTopologySurfaceId;
    private DesktopPageTopologyToken _touchPageTopologyToken;
    private DesktopMonitorTopology _desktopMonitorTopology = new(0, Array.Empty<DesktopMonitorSurfaceMetric>());
    private long _desktopTopologyRevision;
    private int _desktopTopologyReplanCount;
    private int _desktopTopologyDpiMismatchCount;
    private int _desktopTopologySeamBlockedCount;
    private bool _persistDirty;
    private int _persistStorageRetryCount;
    private DateTime _lastAutomationMaintenanceUtc = DateTime.MinValue;
    private string? _uncleanRecoveryPath;
    private bool _abnormalTermination;
    private bool _compatibilityReadOnly;
    private bool _desktopRuntimeStarted;
    private bool _forceNativeIconsVisibleUntilRestart;
    private readonly bool _ignoreCrashLoopOnce;
    private readonly bool _suppressStartupUpdateCheck;
    private readonly string? _managedPolicyCommandLinePath;
    private StartupHealthSnapshot? _startupHealth;
    private IReadOnlyDictionary<string, string> _settingsAuditBaseline = new Dictionary<string, string>();
    private LinkedIconSpacingCoordinator? _linkedIconSpacingCoordinator;

    public AppController(Dispatcher dispatcher, bool safeMode = false, bool ignoreCrashLoopOnce = false, string? managedPolicyPath = null, bool suppressStartupUpdateCheck = false)
    {
        _dispatcher = dispatcher;
        _safeMode = safeMode;
        _ignoreCrashLoopOnce = ignoreCrashLoopOnce;
        _suppressStartupUpdateCheck = suppressStartupUpdateCheck;
        _managedPolicyCommandLinePath = managedPolicyPath;
    }

    public bool IsSafeMode => _safeMode;

    public void Start()
    {
        var startupTimer = Stopwatch.StartNew();
        _state = _store.Load();
        _recycleBinDesktopItem = DesktopSystemItems.CreateRecycleBin(
            _state.Settings.RecycleBinPositionX,
            _state.Settings.RecycleBinPositionY);
        ConfigureSnapshotScreenPreviews();
        _settingsAuditBaseline = SettingsAuditService.Capture(_state.Settings);
        _compatibilityReadOnly = _store.EncounteredUnsupportedState;
        if (_store.EncounteredUnsupportedState && !_safeMode)
        {
            _safeMode = true;
            DiagnosticsLog.Info($"Automatically entering Safe Mode because configuration schema v{_store.UnsupportedStateVersion} is newer than supported v{StateSchema.Current}.");
        }
        _icons.Configure(_state.Settings.IconCacheMaxEntries, _state.Settings.ShowFilePreviews);
        if (_state.Fences.Count == 0) CreateDefaultLayout();
        // Upgrade existing Windows-icon groups in memory, leaving other groups untouched.
        // The normal state checkpoint persists this additive schema-v35 change.
        SynchronizeSystemGroupRecycleBin();
        ResetDesktopFirstSeenGate();
        _uncleanRecoveryPath = _store.BeginSession(_state);
        if (!string.IsNullOrWhiteSpace(_uncleanRecoveryPath))
        {
            DiagnosticsLog.Info($"Detected an unclean previous session; recovery checkpoint={_uncleanRecoveryPath}");
            _startupHealth = StartupHealthService.RecordUncleanStart(_state.Settings.CrashLoopWindowMinutes, _state.Settings.CrashLoopThreshold);
        }
        else
        {
            _startupHealth = StartupHealthService.GetSnapshot(_state.Settings.CrashLoopWindowMinutes, _state.Settings.CrashLoopThreshold);
        }
        if (!_safeMode && !_ignoreCrashLoopOnce && _state.Settings.EnableCrashLoopProtection && _startupHealth.ShouldUseSafeMode)
        {
            _safeMode = true;
            DiagnosticsLog.Info($"Automatically entering Safe Mode after {_startupHealth.RecentUncleanStarts} recent unclean starts.");
        }
        // A single effective startup plan makes all four feature combinations
        // explicit without rewriting the user's persisted preferences in Safe Mode.
        var runtimePlan = FeatureRuntimePlan.Create(
            _state.Settings.EnableCyanDeck, _state.Settings.EnableCalendar, _safeMode);
        if (runtimePlan.StartCyanDeck) InitializeQuickHideFeatureRuntime();
        InitializeManagedDeployment();
        _activeMonitorSignature = MonitorConfigurationService.GetSignature();
        _lastStableMonitorCount = MonitorConfigurationService.CaptureMonitors().Count;
        _state.CurrentPage = Math.Clamp(_state.CurrentPage, 0, GetMaxPage());

        if (runtimePlan.IsSafeMode)
        {
            try { _host.SetNativeDesktopIconsVisible(true); }
            catch (Exception ex) { DiagnosticsLog.Error("Safe Mode native desktop icon restoration failed", ex); }
            CreateTray();
            ScheduleAutomaticUpdateCheck();
            startupTimer.Stop();
            DiagnosticsLog.Info($"CyanDeck SAFE MODE; state v{_state.Version}; startup={startupTimer.ElapsedMilliseconds}ms. Desktop hooks, shell parenting, native-icon changes, watchers, and global hotkeys are disabled.");
            _ = TryApplyStartupQuickHideNormalizationAsync();
            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OpenSettingsWindow));
            return;
        }

        // Calendar-only mode never changes Explorer's native icon visibility or runs
        // CyanDeck's file watchers, global hooks or desktop automation.
        if (!runtimePlan.StartCyanDeck)
        {
            _host.PrepareShell();
            StartCalendarModule();
            ApplyStartupSetting(_state.Settings.StartWithWindows);
            CreateTray();
            ScheduleAutomaticUpdateCheck();
            DiagnosticsLog.Info("Calendar-only / controller-only CyanDeck mode started");
            Persist();
            return;
        }

        DesktopBackgroundRecoveryMenu.EnsureRegistered();
        ApplyInitialLayoutAdjustment();
        if (_state.Settings.AutoImportDesktop) ReorganizeDesktop();

        StartDisplayTransitionStabilizer();
        _host.PrepareShell();
        foreach (var fence in _state.Fences) CreateWindow(fence);
        UpdateWindowVisibility();

        ApplyNativeDesktopVisibility();
        ApplyStartupSetting(_state.Settings.StartWithWindows);
        StartDesktopWatchers();
        StartDesktopDoubleClickHook();
        RebuildDesktopMonitorTopology();
        StartDesktopPageGestureHook();
        StartDesktopTouchPageHost();
        StartDesktopFolderPortalDropHost();
        StartLooseDesktopIconHost();
        StartForegroundEventSource();
        StartPeekCommandSurfaces();
        CreateTray();
        ScheduleAutomaticUpdateCheck();
        StartAutomaticBackupTimer();
        StartShellRecovery();
        _desktopRuntimeStarted = true;
        StartCalendarModule();
        _ = TryApplyStartupQuickHideNormalizationAsync();
        StartManagedPolicyWatcher();
        startupTimer.Stop();
        DiagnosticsLog.Info($"State v{_state.Version}; monitors={MonitorConfigurationService.CaptureMonitors().Count}; save debounce={_state.Settings.PersistDebounceMs}ms; icon cache={_state.Settings.IconCacheMaxEntries}; startup={startupTimer.ElapsedMilliseconds}ms");
        Persist();
    }

    private void InitializeManagedDeployment()
    {
        // Product branding must not redirect the managed-policy recovery cache
        // before every writer is migrated to the same active profile root.
        var cachePath = Path.Combine(DataRootPaths.ActiveRoaming, "managed-policy-cache.json");

        _managedPolicy = new ManagedPolicyCoordinator(
            new ManagedPolicyReader(),
            new ManagedPolicyPlanner(),
            new CyanDeckManagedDeploymentAdapter(
                () => _state,
                ApplyManagedPolicyRuntimeChanges,
                SaveManagedPolicyState),
            new ManagedPolicyCache(cachePath),
            message => DiagnosticsLog.Info(message));

        if (_compatibilityReadOnly)
        {
            _managedPolicyLastError = "호환성 읽기 전용 모드에서는 관리 정책을 적용하지 않습니다.";
            return;
        }

        if (_managedPolicyCommandLinePath is not null && string.IsNullOrWhiteSpace(_managedPolicyCommandLinePath))
        {
            _managedPolicyLastError = "--managed-policy 옵션에 정책 파일 경로가 없습니다.";
            DiagnosticsLog.Info("Managed deployment command-line option was missing its path; no policy was applied.");
            return;
        }

        try
        {
            _managedPolicySourcePath = ManagedPolicySourceResolver.Resolve(_managedPolicyCommandLinePath, AppContext.BaseDirectory);
        }
        catch (ArgumentException ex)
        {
            _managedPolicyLastError = ex.Message;
            DiagnosticsLog.Info("Managed deployment source resolution failed; no policy was applied.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_managedPolicySourcePath))
        {
            _managedPolicyLastError = null;
            return;
        }

        ReloadManagedPolicyFromConfiguredSource(allowCacheFallback: true, interactive: false);
    }

    private void StartManagedPolicyWatcher()
    {
        _managedPolicyWatcher?.Dispose();
        _managedPolicyWatcher = null;
        if (_safeMode || _compatibilityReadOnly || string.IsNullOrWhiteSpace(_managedPolicySourcePath)) return;

        try
        {
            _managedPolicyWatcher = new ManagedPolicyFileWatcher(
                _managedPolicySourcePath,
                () => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    ReloadManagedPolicyFromConfiguredSource(allowCacheFallback: false, interactive: false))),
                message => DiagnosticsLog.Info(message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticsLog.Info($"Managed deployment watcher could not start: {ex.GetType().Name}");
            _managedPolicyLastError = "관리 정책 자동 다시 불러오기를 시작하지 못했습니다. 수동 다시 불러오기를 사용할 수 있습니다.";
        }
    }

    private ManagedPolicyApplyResult? ReloadManagedPolicyFromConfiguredSource(bool allowCacheFallback, bool interactive)
    {
        if (_managedPolicy is null || _compatibilityReadOnly) return null;

        if (string.IsNullOrWhiteSpace(_managedPolicySourcePath))
        {
            try
            {
                _managedPolicySourcePath = ManagedPolicySourceResolver.Resolve(_managedPolicyCommandLinePath, AppContext.BaseDirectory);
            }
            catch (ArgumentException ex)
            {
                _managedPolicyLastError = ex.Message;
                if (interactive)
                    System.Windows.MessageBox.Show(_managedPolicyLastError, "CyanDeck 관리 정책", MessageBoxButton.OK, MessageBoxImage.Warning);
                return ManagedPolicyApplyResult.Failed(_managedPolicyLastError, _managedPolicy.ActivePolicySha256);
            }
        }

        if (string.IsNullOrWhiteSpace(_managedPolicySourcePath))
        {
            _managedPolicyLastError = "구성된 관리 정책 파일이 없습니다.";
            if (interactive)
                System.Windows.MessageBox.Show(_managedPolicyLastError, "CyanDeck 관리 정책", MessageBoxButton.OK, MessageBoxImage.Information);
            return ManagedPolicyApplyResult.NoChange(_managedPolicy.ActivePolicySha256);
        }

        var result = _managedPolicy.ReloadFromSource(_managedPolicySourcePath, allowCacheFallback);
        _managedPolicyLastReloadUtc = DateTime.UtcNow;
        _managedPolicyLastError = result.Success ? null : result.Error;
        if (result.Success && _desktopRuntimeStarted && _managedPolicyWatcher is null)
            StartManagedPolicyWatcher();

        if (interactive)
        {
            var text = result.Success
                ? result.Changed ? "관리 정책을 다시 불러와 적용했습니다." : "관리 정책이 이미 최신 상태입니다."
                : $"관리 정책을 다시 불러오지 못했습니다. 현재 적용 중인 정책은 유지됩니다.\n\n{result.Error}";
            System.Windows.MessageBox.Show(text, "CyanDeck 관리 정책", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        RefreshOpenManagedPolicyUi();
        return result;
    }

    private void ReloadManagedPolicyInteractive()
        => ReloadManagedPolicyFromConfiguredSource(allowCacheFallback: false, interactive: true);

    private void ApplyManagedPolicyRuntimeChanges()
    {
        if (!PrepareQuickHideEnabledChange(_state.Settings.EnableQuickHide, showNotice: false))
            throw new InvalidOperationException("Managed Quick-hide disable could not restore normal visibility.");

        AuditSettingsChanges("ManagedDeployment");
        _icons.Configure(_state.Settings.IconCacheMaxEntries, _state.Settings.ShowFilePreviews);
        ConfigureSnapshotScreenPreviews();

        if (_desktopRuntimeStarted && !_safeMode)
        {
            ApplyNativeDesktopVisibility();
            RefreshDesktopTouchPageHost();
            RefreshDesktopFolderPortalDropHost();
            UpdateWindowVisibility();
            _pageGesture?.UpdateSettings(_state.Settings);
            RefreshPeekCommandSurfaces();
            RefreshDesktopWatchers(force: true, reconcile: false);
            if (_state.Settings.AutoImportDesktop) ScheduleDesktopReconcile(120);
            PruneSnapshots();
            _looseDesktopHost?.RefreshAllIcons();
            foreach (var window in _windows.ToArray())
            {
                window.RefreshManagedPolicyCapabilities();
                window.RefreshFromModel();
            }
        }

        RefreshOpenManagedPolicyUi();
        if (_trayController is not null) RebuildTrayMenu();
    }

    private void SaveManagedPolicyState()
    {
        if (_compatibilityReadOnly) throw new InvalidOperationException("Compatibility read-only mode blocks managed-policy persistence.");
        // Managed policy changes can occur before the normal debounce timer has
        // been initialized. Use the same recoverable persistence path as every
        // other layout change, including clean first-run installations.
        PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true);
    }

    private void RefreshOpenManagedPolicyUi()
    {
        foreach (var window in Application.Current.Windows.OfType<SettingsWindow>().ToArray())
            window.RefreshManagedPolicyState();
    }

    private bool IsManagedSettingLocked(string key) => _managedPolicy?.IsSettingLocked(key) == true;

    private bool IsManagedCapabilityAllowed(string capability)
        => _managedPolicy?.IsCapabilityAllowed(capability) != false;

    private bool EnsureManagedCapability(string capability, string actionLabel, bool showNotice = true)
    {
        if (IsManagedCapabilityAllowed(capability)) return true;
        if (showNotice)
        {
            System.Windows.MessageBox.Show(
                $"'{actionLabel}' 기능은 현재 CyanDeck 관리 정책에 의해 비활성화되어 있습니다.",
                "CyanDeck 관리 정책",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        return false;
    }

    private string GetManagedPolicyStatusText()
    {
        if (_managedPolicy is null) return "관리 정책: 초기화되지 않음";
        var summary = _managedPolicy.GetDiagnosticSummary();
        if (!summary.Active)
        {
            return string.IsNullOrWhiteSpace(_managedPolicyLastError)
                ? "관리 정책: 사용 안 함"
                : $"관리 정책: 적용되지 않음 · {_managedPolicyLastError}";
        }

        var source = summary.SourceKind switch
        {
            ManagedPolicySourceKind.LocalFile => "로컬 파일",
            ManagedPolicySourceKind.UncFile => "네트워크(UNC) 파일",
            ManagedPolicySourceKind.Cache => "마지막 유효 캐시",
            _ => "알 수 없음"
        };
        var style = summary.DeploymentStyle switch
        {
            ManagedDeploymentStyle.Locked => "잠금",
            ManagedDeploymentStyle.Flexible => "유연",
            ManagedDeploymentStyle.Unlocked => "초기값 템플릿",
            _ => "알 수 없음"
        };
        var reload = _managedPolicyLastReloadUtc is DateTime utc ? $" · 마지막 확인 {utc.ToLocalTime():g}" : string.Empty;
        var warning = string.IsNullOrWhiteSpace(_managedPolicyLastError) ? string.Empty : " · 원본 다시 불러오기 실패(현재 정책 유지)";
        return $"관리 정책: {style} · {source} · 해시 {summary.HashPrefix ?? "없음"} · 잠금 설정 {summary.LockedSettingCount}개 · 기능 재정의 {summary.CapabilityOverrideCount}개{reload}{warning}";
    }

    private void ReapplyManagedPolicyAfterStateReplacement()
    {
        if (_managedPolicy is null || !_managedPolicy.GetDiagnosticSummary().Active) return;
        var result = _managedPolicy.ReapplyActivePolicy();
        _managedPolicyLastReloadUtc = DateTime.UtcNow;
        _managedPolicyLastError = result.Success ? null : result.Error;
        if (!result.Success)
            DiagnosticsLog.Info("Managed deployment could not be re-applied after state replacement; previous enforcement metadata remains active.");
    }

    private void CreateDefaultLayout()
    {
        var wa = SystemParameters.WorkArea;
        var left = wa.Left + 36;
        var top = wa.Top + 46;
        var gap = 18;
        var w = Math.Min(365, Math.Max(220, (wa.Width - 72 - gap * 2) / 3));
        var h = Math.Min(355, Math.Max(240, wa.Height / 2.5));

        _state.Fences =
        [
            new DeckFence
            {
                Title = "앱", X = left, Y = top, Width = w, Height = h, ExpandedHeight = h,
                Accent = "#6FA8FF", Background = "#161A20",
                Tabs =
                [
                    new DeckTab { Title = "앱", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "app" }] },
                    new DeckTab { Title = "설치", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "installer" }] }
                ]
            },
            new DeckFence
            {
                Title = "작업", X = left + w + gap, Y = top, Width = w, Height = h, ExpandedHeight = h,
                Accent = "#9B8CFF", Background = "#181820",
                Tabs =
                [
                    new DeckTab { Title = "문서", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "document" }] },
                    new DeckTab { Title = "PDF", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "pdf" }] },
                    new DeckTab { Title = "시트", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "spreadsheet" }] },
                    new DeckTab { Title = "발표", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "presentation" }] },
                    new DeckTab { Title = "텍스트", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "text" }] },
                    new DeckTab { Title = "코드", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "code" }] },
                    new DeckTab { Title = "폴더", Rules = [new DeckRule { Field = RuleField.Folder }] },
                    new DeckTab { Title = "기타" }
                ]
            },
            new DeckFence
            {
                Title = "미디어", X = left + (w + gap) * 2, Y = top, Width = w, Height = h, ExpandedHeight = h,
                Accent = "#73D7B0", Background = "#141C1B",
                Tabs =
                [
                    new DeckTab { Title = "이미지", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "image" }] },
                    new DeckTab { Title = "동영상", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "video" }] },
                    new DeckTab { Title = "오디오", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "audio" }] },
                    new DeckTab { Title = "압축", Rules = [new DeckRule { Field = RuleField.FileCategoryIs, Pattern = "archive" }] }
                ]
            },
            CreateSystemIconGroup(left + (w + gap) * 2, top + h + gap, w, Math.Min(260, h), pageIndex: 0)
        ];
    }

    private DeckFence CreateSystemIconGroup(double x, double y, double width, double height, int pageIndex)
        => new()
        {
            Title = "Windows 기본 아이콘",
            X = x, Y = y, Width = width, Height = height, ExpandedHeight = height,
            PageIndex = pageIndex,
            Accent = "#77C9FF", Background = "#161C25",
            Tabs = [new DeckTab { Title = "시스템", Items = DesktopSystemItems.CreateGroupItems(_state.Settings.ShowRecycleBinInSystemGroup) }],
        };

    // Only a known system-icon group is eligible. Never change arbitrary collections
    // or the separately hosted Recycle Bin's identity, position, or preferences.
    private bool SynchronizeSystemGroupRecycleBin()
    {
        var changed = false;
        foreach (var fence in _state.Fences.Where(IsSystemIconGroup))
        {
            var collectionTabs = fence.Tabs.Where(t => t.Kind == DeckTabKind.Collection).ToArray();
            if (collectionTabs.Length == 0) continue;
            var existing = collectionTabs.SelectMany(t => t.Items).Any(DesktopSystemItems.IsRecycleBin);
            if (_state.Settings.ShowRecycleBinInSystemGroup && !existing)
            {
                var target = collectionTabs.FirstOrDefault(t => t.Title == "시스템") ?? collectionTabs[0];
                target.Items.Add(DesktopSystemItems.CreateRecycleBin(null, null));
                changed = true;
            }
            else if (!_state.Settings.ShowRecycleBinInSystemGroup && existing)
            {
                foreach (var tab in collectionTabs)
                    changed |= tab.Items.RemoveAll(DesktopSystemItems.IsRecycleBin) > 0;
            }
        }
        return changed;
    }

    private static bool IsSystemIconGroup(DeckFence fence)
        => fence.Tabs.Any(t => t.Kind == DeckTabKind.Collection
            && t.Items.Count(DesktopSystemItems.IsSupportedVirtualItem) >= 2)
            && (fence.Title == "Windows 기본 아이콘"
                || fence.Tabs.Any(t => t.Title == "시스템"));

    // Existing layouts are preserved. The user can add this group explicitly from the tray.
    private void ResetStandaloneRecycleBinPosition()
    {
        // Explicit repair only: preserve existing icon coordinates during upgrades.
        _recycleBinDesktopItem ??= DesktopSystemItems.CreateRecycleBin(null, null);
        var area = Forms.Screen.PrimaryScreen?.WorkingArea
            ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var inset = (int)Math.Ceiling(LooseDesktopIconLayoutProfile.EdgeInsetDip);
        _recycleBinDesktopItem.PositionX = Math.Max(area.Left,
            area.Right - (int)Math.Ceiling(LooseDesktopIconLayoutProfile.RecycleBinWidthDip) - inset);
        _recycleBinDesktopItem.PositionY = Math.Max(area.Top,
            area.Bottom - (int)Math.Ceiling(LooseDesktopIconLayoutProfile.RecycleBinHeightDip) - inset);
        _state.Settings.RecycleBinPositionX = _recycleBinDesktopItem.PositionX;
        _state.Settings.RecycleBinPositionY = _recycleBinDesktopItem.PositionY;
        RefreshLooseDesktopIconHost();
        Persist();
        DiagnosticsLog.Info("Standalone Recycle Bin restored to visible lower-right work area.");
    }

    private void AddSystemIconGroup()
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "Windows 기본 아이콘 그룹 추가")) return;
        var existing = _state.Fences.FirstOrDefault(IsSystemIconGroup);
        if (existing is not null)
        {
            System.Windows.MessageBox.Show("Windows 기본 아이콘이 이미 그룹에 있습니다. 기존 그룹과 배치를 유지합니다.",
                "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var work = SystemParameters.WorkArea;
        var w = Math.Min(360d, Math.Max(220d, work.Width * 0.30));
        var h = Math.Min(260d, Math.Max(190d, work.Height * 0.33));
        // Keep the new group away from the standalone Recycle Bin's default
        // lower-right placement. Existing group/icon coordinates are never moved.
        var x = Math.Min(Math.Max(work.Left, work.Left + 35), Math.Max(work.Left, work.Right - w));
        var y = Math.Max(work.Top, work.Bottom - h - 35);
        var fence = CreateSystemIconGroup(x, y, w, h, _state.CurrentPage);
        _state.Fences.Add(fence);
        CreateWindow(fence);
        // Scope visibility updates to this new group. Refreshing every loose-icon HWND
        // here can hide an otherwise visible standalone Recycle Bin during a transient
        // Explorer WorkerW lookup failure.
        var createdWindow = _windows.FirstOrDefault(w => w.Model.Id == fence.Id);
        if (createdWindow is not null)
        {
            var show = FenceVisibilityPolicy.ShouldShow(
                fence.PageIndex, fence.ShowOnAllPages, fence.AlwaysVisible,
                _state.CurrentPage, _quickHidden, _state.Settings.EnableQuickHide,
                _state.Settings.QuickHideMode == DeckQuickHideMode.UngroupedIconsOnly);
            if (!show && createdWindow.IsVisible) createdWindow.Hide();
        }
        Persist();
        RebuildTrayMenu();
    }

    private void ImportExistingDesktop()
    {
        foreach (var path in RuleEngine.EnumerateDesktopItems()) ImportPath(path, false);
    }

    private void ReorganizeDesktop(bool forceRules = false)
    {
        var useRules = forceRules || _state.Settings.KeepRulesUpdatedAtAllTimes;
        var roots = DesktopDirectoryService.GetRoots();
        var paths = RuleEngine.EnumerateDesktopItems()
            .Select(DesktopDirectoryService.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collectionTabs = _state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection).ToArray();
        var changed = false;

        var looseByPath = _state.LooseDesktopItems
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .GroupBy(i => DesktopDirectoryService.Normalize(i.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var stale in looseByPath.Keys.Where(path =>
                     !paths.Contains(path)
                     && (!looseByPath[path].PinnedToDesktop
                         || (!File.Exists(path) && !Directory.Exists(path)))).ToArray())
        {
            _state.LooseDesktopItems.Remove(looseByPath[stale]);
            looseByPath.Remove(stale);
            changed = true;
        }

        // Legacy automatic classification remains the default for upgraded users. Explicit
        // v7.44 default-placement mode only affects newly observed Desktop/Public Desktop items.
        if ((!_state.Settings.UseDefaultDesktopPlacement && !_state.Settings.LeaveNewDesktopItemsUngrouped) || forceRules)
        {
            // Existing loose references are not thrown away: move the same stable item into
            // its rule-selected group so ID/open-count/Quick-hide metadata survive the move.
            foreach (var item in _state.LooseDesktopItems.ToArray())
            {
                // A user explicitly detached this item from a group. Keep it as a standalone
                // desktop icon until the user explicitly auto-classifies/flicks it back.
                if (item.PinnedToDesktop) continue;
                var path = DesktopDirectoryService.Normalize(item.Path);
                var desired = RuleEngine.PickTab(_state, path, includeRules: useRules);
                if (desired is null) continue;
                if (!desired.Items.Any(i => string.Equals(DesktopDirectoryService.Normalize(i.Path), path, StringComparison.OrdinalIgnoreCase)))
                {
                    item.Path = path;
                    item.PositionX = null;
                    item.PositionY = null;
                    desired.Items.Add(item);
                }
                _state.LooseDesktopItems.Remove(item);
                looseByPath.Remove(path);
                changed = true;
            }
        }

        // Remove references to top-level desktop items that no longer exist. References
        // to files elsewhere (including manually added paths) are intentionally retained.
        foreach (var tab in collectionTabs)
        {
            changed |= tab.Items.RemoveAll(i =>
                DesktopDirectoryService.IsDirectChildOfAnyRoot(i.Path, roots)
                && !paths.Contains(DesktopDirectoryService.Normalize(i.Path))) > 0;
        }

        var eligibleTargetKeys = BuildEligibleDefaultPlacementTargetKeys();
        var placementOptions = _state.Settings.UseDefaultDesktopPlacement
            ? DefaultDesktopPlacementSettingsAdapter.Normalize(
                _state.Settings.DefaultDesktopPlacementKind,
                _state.Settings.DefaultDesktopPlacementGroupId,
                _state.Settings.DefaultDesktopPlacementTabId,
                _state.Settings.ActivateDefaultDestinationTab,
                eligibleTargetKeys)
            : DefaultDesktopPlacementOptions.Defaults;
        var placementCoordinator = new DefaultDesktopPlacementCoordinator(
            DefaultPlacementTargetExists,
            MoveManagedDesktopReferenceToTarget,
            ActivateDefaultPlacementTarget);

        foreach (var path in paths)
        {
            var isNewlyObserved = _desktopFirstSeenGate?.MarkAndIsNew(path) ?? false;
            var matches = collectionTabs
                .SelectMany(t => t.Items.Where(i => string.Equals(DesktopDirectoryService.Normalize(i.Path), path, StringComparison.OrdinalIgnoreCase))
                    .Select(i => (Tab: t, Item: i)))
                .ToArray();

            if (looseByPath.TryGetValue(path, out var pinnedLoose) && pinnedLoose.PinnedToDesktop)
                continue;

            if (!forceRules && _state.Settings.UseDefaultDesktopPlacement)
            {
                if (isNewlyObserved)
                {
                    var ruleTab = RuleEngine.PickRuleTab(_state, path);
                    CollectionTabTarget? ruleTarget = ruleTab is null ? null : BuildCollectionTarget(ruleTab);
                    var decision = DefaultDesktopPlacementPlanner.Plan(new DesktopPlacementRequest(
                        ItemIdentityKey: path,
                        IsNewlyObserved: true,
                        IsManagedDesktopReference: true,
                        AutoImportEnabled: _state.Settings.AutoImportDesktop,
                        RuleState: ruleTarget is null ? RulePlacementState.NoMatch : RulePlacementState.ValidCollectionTarget,
                        RuleTarget: ruleTarget,
                        Options: placementOptions,
                        EligibleCollectionTargetKeys: eligibleTargetKeys));

                    if (decision.MovesReference)
                    {
                        var managedId = GetOrCreateManagedDesktopReferenceId(path, matches, looseByPath);
                        if (placementCoordinator.TryApply(managedId, decision))
                        {
                            looseByPath.Remove(path);
                            changed = true;
                            continue;
                        }

                        // Commit-time deletion/invalid target: keep the managed reference visible
                        // as a loose desktop icon rather than falling through to another target.
                        changed |= EnsureLooseDesktopReference(path, matches, looseByPath);
                        continue;
                    }

                    if (decision.Kind is DesktopPlacementDecisionKind.LeaveOnDesktop
                        or DesktopPlacementDecisionKind.LeaveOnDesktopInvalidRuleTarget
                        or DesktopPlacementDecisionKind.LeaveOnDesktopInvalidDefaultTarget)
                    {
                        changed |= EnsureLooseDesktopReference(path, matches, looseByPath);
                        continue;
                    }
                }
                else if (matches.Length == 0)
                {
                    // Explicit default destinations are intentionally non-retroactive. Existing
                    // unmanaged desktop items become loose references so they stay visible, but
                    // are not moved into the newly selected destination.
                    changed |= EnsureLooseDesktopReference(path, matches, looseByPath);
                    continue;
                }

                // Existing loose items stay where the user left them until Apply rules now.
                if (matches.Length == 0 && looseByPath.ContainsKey(path))
                    continue;
            }

            if (matches.Length == 0 && !forceRules && _state.Settings.LeaveNewDesktopItemsUngrouped && looseByPath.ContainsKey(path))
                continue;

            // KeepRulesUpdatedAtAllTimes controls reclassification of existing items. A newly
            // observed desktop item must still honor the user's explicit rules on first import;
            // otherwise turning continuous maintenance off silently disables auto-group rules.
            var includeRulesForPath = useRules || isNewlyObserved;
            var desired = RuleEngine.PickTab(_state, path, includeRules: includeRulesForPath);
            if (desired is null) continue;

            if (matches.Length == 0)
            {
                if (looseByPath.Remove(path, out var looseItem))
                {
                    _state.LooseDesktopItems.Remove(looseItem);
                    looseItem.Path = path;
                    looseItem.PositionX = null;
                    looseItem.PositionY = null;
                    desired.Items.Add(looseItem);
                }
                else
                {
                    desired.Items.Add(new DeckItem { Path = path });
                }
                changed = true;
                continue;
            }

            if (looseByPath.Remove(path, out var duplicateLoose))
            {
                _state.LooseDesktopItems.Remove(duplicateLoose);
                changed = true;
            }

            if (matches.Any(x => ReferenceEquals(x.Tab, desired))) continue;
            var managed = matches[0];
            if (managed.Tab.Items.Remove(managed.Item))
            {
                managed.Item.Path = path;
                managed.Item.PositionX = null;
                managed.Item.PositionY = null;
                desired.Items.Add(managed.Item);
                changed = true;
            }
        }

        SyncLegacyUngroupedPaths();
        _lastAutomationMaintenanceUtc = DateTime.UtcNow;
        if (changed)
        {
            foreach (var w in _windows) w.RefreshItems();
            Persist();
        }
        RefreshLooseDesktopIconHost();
    }

    private HashSet<string> BuildEligibleDefaultPlacementTargetKeys()
    {
        return _state.Fences
            .SelectMany(f => f.Tabs.Where(t => t.Kind == DeckTabKind.Collection)
                .Select(t => DefaultDesktopPlacementPlanner.BuildTargetKey(
                    new CollectionTabTarget(f.Id.ToString("D"), t.Id.ToString("D")))))
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.Ordinal);
    }

    private CollectionTabTarget? BuildCollectionTarget(DeckTab tab)
    {
        var fence = _state.Fences.FirstOrDefault(f => f.Tabs.Any(t => t.Id == tab.Id));
        return fence is null ? null : new CollectionTabTarget(fence.Id.ToString("D"), tab.Id.ToString("D"));
    }

    private bool DefaultPlacementTargetExists(CollectionTabTarget target)
    {
        if (!Guid.TryParse(target.GroupId, out var fenceId) || !Guid.TryParse(target.TabId, out var tabId)) return false;
        var fence = _state.Fences.FirstOrDefault(f => f.Id == fenceId);
        return fence?.Tabs.Any(t => t.Id == tabId && t.Kind == DeckTabKind.Collection) == true;
    }

    private string GetOrCreateManagedDesktopReferenceId(
        string path,
        IReadOnlyList<(DeckTab Tab, DeckItem Item)> matches,
        Dictionary<string, DeckItem> looseByPath)
    {
        if (looseByPath.TryGetValue(path, out var loose)) return loose.Id.ToString("D");
        if (matches.Count > 0) return matches[0].Item.Id.ToString("D");

        var item = new DeckItem { Path = path };
        _state.LooseDesktopItems.Add(item);
        looseByPath[path] = item;
        return item.Id.ToString("D");
    }

    private bool EnsureLooseDesktopReference(
        string path,
        IReadOnlyList<(DeckTab Tab, DeckItem Item)> matches,
        Dictionary<string, DeckItem> looseByPath)
    {
        if (looseByPath.ContainsKey(path)) return false;

        DeckItem item;
        if (matches.Count > 0)
        {
            item = matches[0].Item;
            foreach (var match in matches)
                match.Tab.Items.RemoveAll(i => i.Id == item.Id);
        }
        else
        {
            item = new DeckItem { Path = path };
        }
        item.Path = path;
        item.PositionX = null;
        item.PositionY = null;
        _state.LooseDesktopItems.Add(item);
        looseByPath[path] = item;
        return true;
    }

    private bool MoveManagedDesktopReferenceToTarget(string managedReferenceId, CollectionTabTarget target)
    {
        if (!Guid.TryParse(managedReferenceId, out var itemId)
            || !Guid.TryParse(target.GroupId, out var fenceId)
            || !Guid.TryParse(target.TabId, out var tabId)) return false;

        var fence = _state.Fences.FirstOrDefault(f => f.Id == fenceId);
        var destination = fence?.Tabs.FirstOrDefault(t => t.Id == tabId && t.Kind == DeckTabKind.Collection);
        if (destination is null) return false;

        DeckItem? item = _state.LooseDesktopItems.FirstOrDefault(i => i.Id == itemId);
        item ??= _state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection)
            .SelectMany(t => t.Items).FirstOrDefault(i => i.Id == itemId);
        if (item is null) return false;

        _state.LooseDesktopItems.RemoveAll(i => i.Id == itemId);
        foreach (var tab in _state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection))
            tab.Items.RemoveAll(i => i.Id == itemId);

        item.PinnedToDesktop = false;
        item.PositionX = null;
        item.PositionY = null;
        if (!destination.Items.Any(i => i.Id == itemId)) destination.Items.Add(item);
        return true;
    }

    private void ActivateDefaultPlacementTarget(CollectionTabTarget target)
    {
        if (!Guid.TryParse(target.GroupId, out var fenceId) || !Guid.TryParse(target.TabId, out var tabId)) return;
        var fence = _state.Fences.FirstOrDefault(f => f.Id == fenceId);
        if (fence is null) return;
        var index = fence.Tabs.FindIndex(t => t.Id == tabId && t.Kind == DeckTabKind.Collection);
        if (index < 0) return;
        fence.SelectedTabIndex = index;
        _windows.FirstOrDefault(w => w.Model.Id == fence.Id)?.RefreshFromModel();
    }

    private void ResetDesktopFirstSeenGate()
    {
        var baseline = RuleEngine.EnumerateDesktopItems()
            .Select(DesktopDirectoryService.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _desktopFirstSeenGate = new DesktopFirstSeenGate(baseline, StringComparer.OrdinalIgnoreCase);
    }

    private void SyncLegacyUngroupedPaths()
    {
        _state.UngroupedDesktopPaths = _state.LooseDesktopItems
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .Select(i => DesktopDirectoryService.Normalize(i.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private DeckItem? FindLooseDesktopItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = DesktopDirectoryService.Normalize(path);
        return _state.LooseDesktopItems.FirstOrDefault(i =>
            string.Equals(DesktopDirectoryService.Normalize(i.Path), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void MarkNewDesktopItemUngrouped(string path)
    {
        if (!_state.Settings.AutoImportDesktop || !_state.Settings.LeaveNewDesktopItemsUngrouped || string.IsNullOrWhiteSpace(path)) return;
        var normalized = DesktopDirectoryService.Normalize(path);
        if (FindLooseDesktopItem(normalized) is not null) return;
        if (_state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection).SelectMany(t => t.Items)
            .Any(i => string.Equals(DesktopDirectoryService.Normalize(i.Path), normalized, StringComparison.OrdinalIgnoreCase))) return;
        _state.LooseDesktopItems.Add(new DeckItem { Path = normalized });
        SyncLegacyUngroupedPaths();
        RefreshLooseDesktopIconHost();
        Persist();
    }

    private void RemoveUngroupedDesktopMarker(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = DesktopDirectoryService.Normalize(path);
        if (_state.LooseDesktopItems.RemoveAll(i => string.Equals(DesktopDirectoryService.Normalize(i.Path), normalized, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            SyncLegacyUngroupedPaths();
            RefreshLooseDesktopIconHost();
            Persist();
        }
    }

    private void RenameUngroupedDesktopMarker(string oldPath, string newPath)
    {
        var oldNormalized = DesktopDirectoryService.Normalize(oldPath);
        var item = _state.LooseDesktopItems.FirstOrDefault(i => string.Equals(DesktopDirectoryService.Normalize(i.Path), oldNormalized, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        item.Path = DesktopDirectoryService.Normalize(newPath);
        SyncLegacyUngroupedPaths();
        RefreshLooseDesktopIconHost();
        Persist();
    }

    private void ImportPath(string path, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var full = path;
        try { full = Path.GetFullPath(path); } catch { }
        full = DesktopDirectoryService.Normalize(full);

        var duplicate = _state.Fences.SelectMany(f => f.Tabs)
            .Where(t => t.Kind == DeckTabKind.Collection)
            .SelectMany(t => t.Items)
            .Any(i => string.Equals(DesktopDirectoryService.Normalize(i.Path), full, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            RemoveUngroupedDesktopMarker(full);
            return;
        }

        var tab = RuleEngine.PickTab(_state, full);
        if (tab is null) return;
        var looseItem = FindLooseDesktopItem(full);
        if (looseItem is not null)
        {
            _state.LooseDesktopItems.Remove(looseItem);
            looseItem.Path = full;
            looseItem.PositionX = null;
            looseItem.PositionY = null;
            tab.Items.Add(looseItem);
            SyncLegacyUngroupedPaths();
            RefreshLooseDesktopIconHost();
        }
        else
        {
            tab.Items.Add(new DeckItem { Path = full });
        }
        RefreshTab(tab.Id);
        if (persist) Persist();
    }

    private void RemovePath(string path)
    {
        var changed = false;
        foreach (var tab in _state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection))
            changed |= tab.Items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!changed) return;
        foreach (var w in _windows) w.RefreshItems();
        Persist();
    }



    private void OnFenceMoveCompleted(FenceWindow source)
    {
        if (_peek || !_windows.Contains(source)) return;
        if (TryMoveFenceAcrossPageEdge(source)) return;
        if (source.Model.RolledUp) return;
        var sourceRect = new System.Windows.Rect(source.Left, source.Top, source.ActualWidth, source.ActualHeight);
        var center = new System.Windows.Point(sourceRect.Left + sourceRect.Width / 2, sourceRect.Top + sourceRect.Height / 2);
        var target = _windows
            .Where(w => !ReferenceEquals(w, source) && w.IsVisible)
            .Select(w => new { Window = w, Rect = new System.Windows.Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) })
            .Select(x => new { x.Window, x.Rect, Intersection = System.Windows.Rect.Intersect(sourceRect, x.Rect) })
            .Where(x => !x.Intersection.IsEmpty && x.Rect.Contains(center))
            .OrderByDescending(x => x.Intersection.Width * x.Intersection.Height)
            .FirstOrDefault();
        if (target is not null && _state.Settings.EnableTabGroups)
        {
            var overlap = target.Intersection.Width * target.Intersection.Height;
            var sourceArea = Math.Max(1, sourceRect.Width * sourceRect.Height);
            if (overlap / sourceArea >= 0.35)
            {
                if (_state.Settings.ConfirmTabGroupMerge)
                {
                    var answer = System.Windows.MessageBox.Show(
                        $"Combine '{source.Model.Title}' with '{target.Window.Model.Title}' as tabs?",
                        "CyanDeck",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes)
                    {
                        source.ApplyModelLayout();
                        return;
                    }
                }
                if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "그룹을 탭으로 결합", showNotice: false))
                {
                    source.ApplyModelLayout();
                    return;
                }
                MergeFences(source, target.Window);
                return;
            }
        }
        if (TrySnapFenceToPeers(source)) Persist();
    }


    private System.Windows.Point ResolveFenceMovePosition(FenceWindow source, double left, double top)
    {
        if (!_state.Settings.EnableSmartGroupSnapping || source.Model.Locked)
            return new System.Windows.Point(left, top);

        const double threshold = 14d;
        var gap = Math.Clamp(_state.Settings.GroupSnapGap, 0, 64);
        var width = Math.Max(1, source.ActualWidth > 0 ? source.ActualWidth : source.Width);
        var height = Math.Max(1, source.ActualHeight > 0 ? source.ActualHeight : source.Height);
        var sourceRect = new System.Windows.Rect(left, top, width, height);
        var bestX = left;
        var bestY = top;
        var bestDx = threshold + 1;
        var bestDy = threshold + 1;

        foreach (var peer in _windows.Where(w => !ReferenceEquals(w, source) && w.IsVisible))
        {
            var r = new System.Windows.Rect(peer.Left, peer.Top, peer.ActualWidth, peer.ActualHeight);
            var verticalNear = sourceRect.Bottom >= r.Top - threshold && sourceRect.Top <= r.Bottom + threshold;
            var horizontalNear = sourceRect.Right >= r.Left - threshold && sourceRect.Left <= r.Right + threshold;

            if (verticalNear)
            {
                foreach (var candidate in new[] { r.Left - gap - sourceRect.Width, r.Right + gap, r.Left, r.Right - sourceRect.Width })
                {
                    var d = Math.Abs(left - candidate);
                    if (d <= threshold && d < bestDx) { bestDx = d; bestX = candidate; }
                }
            }
            if (horizontalNear)
            {
                foreach (var candidate in new[] { r.Top - gap - sourceRect.Height, r.Bottom + gap, r.Top, r.Bottom - sourceRect.Height })
                {
                    var d = Math.Abs(top - candidate);
                    if (d <= threshold && d < bestDy) { bestDy = d; bestY = candidate; }
                }
            }
        }

        return new System.Windows.Point(bestDx <= threshold ? bestX : left, bestDy <= threshold ? bestY : top);
    }

    private bool TrySnapFenceToPeers(FenceWindow source)
    {
        if (!_state.Settings.EnableSmartGroupSnapping || source.Model.Locked) return false;
        var snapped = ResolveFenceMovePosition(source, source.Left, source.Top);
        var changed = Math.Abs(snapped.X - source.Left) > 0.01 || Math.Abs(snapped.Y - source.Top) > 0.01;
        if (!changed) return false;
        source.Left = snapped.X;
        source.Top = snapped.Y;
        source.Model.X = source.Left;
        source.Model.Y = source.Top;
        source.EnsureVisibleOnMonitor();
        return true;
    }

    private void MergeFences(FenceWindow source, FenceWindow target)
    {
        if (ReferenceEquals(source, target)) return;
        var selected = source.Model.Tabs.Count == 0 ? null : source.Model.Tabs[Math.Clamp(source.Model.SelectedTabIndex, 0, source.Model.Tabs.Count - 1)];
        foreach (var tab in source.Model.Tabs.ToArray()) target.Model.Tabs.Add(tab);
        if (selected is not null) target.Model.SelectedTabIndex = target.Model.Tabs.IndexOf(selected);
        RemoveFenceWindow(source);
        target.RefreshFromModel();
        Persist();
    }

    private void OnTabTransferRequested(Guid sourceFenceId, Guid tabId, FenceWindow target, int targetIndex)
    {
        var source = _windows.FirstOrDefault(w => w.Model.Id == sourceFenceId);
        if (source is null || ReferenceEquals(source, target)) return;
        var tab = source.Model.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;

        source.Model.Tabs.Remove(tab);
        targetIndex = Math.Clamp(targetIndex, 0, target.Model.Tabs.Count);
        target.Model.Tabs.Insert(targetIndex, tab);
        target.Model.SelectedTabIndex = targetIndex;

        if (source.Model.Tabs.Count == 0) RemoveFenceWindow(source);
        else
        {
            source.Model.SelectedTabIndex = Math.Clamp(source.Model.SelectedTabIndex, 0, source.Model.Tabs.Count - 1);
            source.RefreshFromModel();
        }
        target.RefreshFromModel();
        Persist();
    }

    private void OnTabDetachRequested(FenceWindow source, DeckTab tab, System.Windows.Point screenPoint)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "탭을 새 그룹으로 분리")) return;
        if (!source.Model.Tabs.Contains(tab) || source.Model.Tabs.Count <= 1) return;
        source.Model.Tabs.Remove(tab);
        source.Model.SelectedTabIndex = Math.Clamp(source.Model.SelectedTabIndex, 0, source.Model.Tabs.Count - 1);
        source.RefreshFromModel();

        var fence = new DeckFence
        {
            Title = tab.Title,
            X = screenPoint.X - 90,
            Y = screenPoint.Y - 18,
            Width = Math.Max(260, source.Model.Width),
            Height = Math.Max(180, source.Model.Height),
            ExpandedHeight = Math.Max(180, source.Model.ExpandedHeight),
            Opacity = source.Model.Opacity,
            Background = source.Model.Background,
            Accent = tab.Accent ?? source.Model.Accent,
            CornerRadius = source.Model.CornerRadius,
            IconSize = source.Model.IconSize,
            IconSpacingX = source.Model.IconSpacingX,
            IconSpacingY = source.Model.IconSpacingY,
            IconLabelMode = source.Model.IconLabelMode,
            IconLayoutMode = source.Model.IconLayoutMode,
            RollupActivation = source.Model.RollupActivation,
            RollupHoverDelayMs = source.Model.RollupHoverDelayMs,
            IconTint = source.Model.IconTint,
            TitleFontFamily = source.Model.TitleFontFamily,
            TitleFontSize = source.Model.TitleFontSize,
            TitleForeground = source.Model.TitleForeground,
            IconTintStrength = source.Model.IconTintStrength,
            AutoFade = source.Model.AutoFade,
            TabStyle = source.Model.TabStyle,
            TitlebarMode = source.Model.TitlebarMode,
            PageIndex = source.Model.PageIndex,
            ShowOnAllPages = source.Model.ShowOnAllPages,
            Locked = source.Model.Locked,
            MoveTitlebarToBottomNearTop = source.Model.MoveTitlebarToBottomNearTop,
            Tabs = [tab]
        };
        _state.Fences.Add(fence);
        CreateWindow(fence);
        Persist();
    }

    private void DuplicateFence(FenceWindow source)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "그룹 복제")) return;
        var fence = FenceFactory.Duplicate(source.Model);
        fence.PageIndex = source.Model.PageIndex;
        _state.Fences.Add(fence);
        CreateWindow(fence);
        fence.X = source.Model.X + 24;
        fence.Y = source.Model.Y + 24;
        _windows.Last().ApplyModelLayout();
        Persist();
        RebuildTrayMenu();
    }

    private void SaveFenceTemplate(FenceWindow source)
    {
        if (!EnsureManagedCapability("Templates.Edit", "그룹 템플릿 저장")) return;
        var initial = string.IsNullOrWhiteSpace(source.Model.Title) ? "Group template" : source.Model.Title;
        var name = TextPrompt.Show(source, "그룹 템플릿 저장", "템플릿 이름", initial);
        if (string.IsNullOrWhiteSpace(name)) return;
        var existing = _state.Templates.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null)
        {
            var replace = System.Windows.MessageBox.Show($"기존 템플릿 '{existing.Name}'을(를) 바꾸시겠습니까?", "CyanDeck", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (replace != MessageBoxResult.Yes) return;
            _state.Templates.Remove(existing);
        }
        _state.Templates.Insert(0, FenceFactory.CreateTemplate(source.Model, name));
        if (_state.Templates.Count > 50) _state.Templates.RemoveRange(50, _state.Templates.Count - 50);
        Persist();
        RebuildTrayMenu();
    }

    private void CreateFenceFromTemplate(Guid templateId)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "템플릿에서 그룹 만들기")) return;
        var template = _state.Templates.FirstOrDefault(t => t.Id == templateId);
        if (template is null) return;
        var wa = SystemParameters.WorkArea;
        var offset = (_state.Fences.Count % 8) * 18;
        var fence = FenceFactory.CreateFromTemplate(template, wa.Left + 90 + offset, wa.Top + 90 + offset, _state.CurrentPage);
        _state.Fences.Add(fence);
        CreateWindow(fence);
        Persist();
        RebuildTrayMenu();
    }


    private void ImportDroppedIntoFence(FenceWindow window, string path)
    {
        RemoveUngroupedDesktopMarker(path);
        // FenceWindow adds the path to its selected tab; this callback only keeps other references unique.
        foreach (var other in _state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection))
        {
            if (window.Model.Tabs.Contains(other)) continue;
            other.Items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var w in _windows) w.RefreshItems();
        Persist();
    }

    private void CreateNewFence()
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "새 그룹 만들기")) return;
        var wa = SystemParameters.WorkArea;
        var fence = new DeckFence
        {
            Title = "새 그룹",
            X = wa.Left + 100 + _windows.Count * 18,
            Y = wa.Top + 100 + _windows.Count * 18,
            PageIndex = _state.CurrentPage,
            Tabs = [new DeckTab { Title = "항목" }]
        };
        _state.Fences.Add(fence);
        CreateWindow(fence);
        Persist();
    }

    private void OnDeleteFence(FenceWindow window)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "그룹 삭제")) return;
        if (_state.Fences.Count <= 1)
        {
            System.Windows.MessageBox.Show("그룹은 최소 하나 이상 남아 있어야 합니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RemoveFenceWindow(window);
        Persist();
    }

    private void RefreshTab(Guid tabId)
    {
        foreach (var window in _windows.Where(w => w.Model.Tabs.Any(t => t.Id == tabId))) window.RefreshItems();
    }

    private void TogglePeek() => SetPeek(!_peek);

    private void SetPeek(bool enabled)
    {
        if (_peek == enabled) return;
        _looseDesktopHost?.CancelTouchFlick();
        if (enabled && (_quickHidden || HasQuickHideHiddenVisualResidue()))
            EnsureQuickHideVisible(QuickHideShowReason.RecoveryRepair, QuickHideInvocationSource.Programmatic);
        _peek = enabled;
        foreach (var w in _windows)
        {
            if (_peek)
            {
                _host.SetPeek(w, true);
                w.PrepareForPeek(_state.Settings.PeekRespectWorkingArea);
            }
            else
            {
                w.RestoreAfterPeek();
                _host.SetPeek(w, false);
            }
            w.SetPeekMode(_peek);
        }
        // Leaving Peek drops TOPMOST and returns Fence HWNDs to the non-topmost desktop band.
        // Explorer may reorder its desktop host at the same time, so re-run the normal
        // visibility/z-order path rather than relying on the previous WPF IsVisible state alone.
        if (!_peek)
        {
            foreach (var w in _windows)
            {
                try { _host.EnsureAttached(w); } catch { }
            }
            UpdateWindowVisibility();
            RepairExpectedFenceVisibility();
        }

        RefreshDesktopTouchPageHost();
        RefreshDesktopFolderPortalDropHost();
        RefreshLooseDesktopIconHost();
        UpdateTrayText();
        RebuildTrayMenu();
    }

    private QuickHideFeatureOptions GetQuickHideFeatureOptions() => new()
    {
        Enabled = _state.Settings.EnableQuickHide,
        AlwaysShowIconsAtStartup = _state.Settings.QuickHideAlwaysShowIconsAtStartup,
    };

    private void InitializeQuickHideFeatureRuntime()
    {
        _quickHideConfiguredEnabled = _state.Settings.EnableQuickHide;
        _quickHideFeatureController = new QuickHideFeatureController(
            ReadQuickHideRuntimeSnapshot,
            EnsureQuickHideVisibleAsync);
        _quickHideStartupCoordinator?.Dispose();
        _quickHideStartupCoordinator = new QuickHideStartupVisibilityCoordinator(
            GetQuickHideFeatureOptions,
            ReadQuickHideRuntimeSnapshot,
            EnsureQuickHideVisibleAsync);
        _quickHideStartupStatus = null;
        _quickHideStartupShowReason = null;
    }

    private QuickHideRuntimeSnapshot ReadQuickHideRuntimeSnapshot() => new(
        IsQuickHideActive: _quickHidden,
        HasHiddenVisualResidue: HasQuickHideHiddenVisualResidue(),
        DesktopShellReady: DesktopHost.CanLocateDesktopShell(),
        DisplayTransitionActive: _displayTransition is not null && !_displayTransition.IsStableForUserLayoutAction,
        SafeMode: _safeMode,
        MasterDisabled: false);

    private bool HasQuickHideHiddenVisualResidue()
    {
        if (_quickHidden) return true;
        if (_safeMode || _peek || _desktopDisplay.IsActive) return false;

        foreach (var window in _windows)
        {
            var pageVisible = window.Model.ShowOnAllPages || window.Model.PageIndex == _state.CurrentPage;
            if (pageVisible && !window.IsVisible) return true;
        }

        return _looseDesktopHost?.HasUnexpectedHiddenVisualResidue == true;
    }

    private void ToggleQuickHide(QuickHideInvocationSource source)
    {
        if (!QuickHideCommandGate.ShouldInvokeExistingCommand(
                GetQuickHideFeatureOptions(),
                QuickHideCommandIntent.Toggle,
                source,
                _quickHidden,
                out _))
            return;

        SetQuickHiddenCore(!_quickHidden);
    }

    private bool EnsureQuickHideVisible(QuickHideShowReason reason, QuickHideInvocationSource source)
    {
        var hiddenOrResidue = _quickHidden || HasQuickHideHiddenVisualResidue();
        if (!QuickHideCommandGate.ShouldInvokeExistingCommand(
                GetQuickHideFeatureOptions(),
                QuickHideCommandIntent.ForceShow,
                source,
                hiddenOrResidue,
                out var decision))
            return decision == QuickHideGateDecision.IgnoreAlreadyVisible;

        try
        {
            SetQuickHiddenCore(false);
            return !_quickHidden && !HasQuickHideHiddenVisualResidue();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error($"Quick-hide force-show failed ({reason})", ex);
            return false;
        }
    }

    private Task<bool> EnsureQuickHideVisibleAsync(QuickHideShowReason reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = reason switch
        {
            QuickHideShowReason.PageSwitchAutoUnhide => QuickHideInvocationSource.PageSwitchAutoUnhide,
            QuickHideShowReason.StartupAlwaysShow or QuickHideShowReason.DisabledFeatureStartupRepair => QuickHideInvocationSource.StartupNormalization,
            QuickHideShowReason.RecoveryRepair => QuickHideInvocationSource.RecoveryRepair,
            _ => QuickHideInvocationSource.Programmatic,
        };
        return Task.FromResult(EnsureQuickHideVisible(reason, source));
    }

    private bool PrepareQuickHideEnabledChange(bool requestedEnabled, bool showNotice)
    {
        if (_quickHideFeatureController is null)
        {
            _quickHideConfiguredEnabled = requestedEnabled;
            return true;
        }

        var currentEnabled = _quickHideConfiguredEnabled;
        var result = _quickHideFeatureController
            .PrepareEnabledChangeAsync(currentEnabled, requestedEnabled)
            .GetAwaiter()
            .GetResult();

        if (result.Status == QuickHideEnabledChangeStatus.ShowFailedDoNotPersist)
        {
            _state.Settings.EnableQuickHide = currentEnabled;
            if (showNotice)
            {
                System.Windows.MessageBox.Show(
                    "빠른 숨김을 끄기 전에 바탕화면을 정상 표시 상태로 복구하지 못했습니다. 빠른 숨김 설정은 기존 상태로 유지됩니다.",
                    "CyanDeck",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }

        _quickHideConfiguredEnabled = result.ValueToPersist;
        _state.Settings.EnableQuickHide = result.ValueToPersist;
        return true;
    }

    private async Task TryApplyStartupQuickHideNormalizationAsync()
    {
        if (_quickHideStartupCoordinator is null) return;
        try
        {
            var result = await _quickHideStartupCoordinator.TryApplyAsync().ConfigureAwait(true);
            _quickHideStartupStatus = result.Status;
            _quickHideStartupShowReason = result.ShowReason;
            DiagnosticsLog.Info($"Quick-hide startup normalization: status={result.Status}; normalized={result.VisibilityWasNormalized}; reason={result.ShowReason?.ToString() ?? "none"}");
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Quick-hide startup normalization failed", ex);
        }
    }

    private void SetQuickHiddenCore(bool hidden)
    {
        if (hidden && !_state.Settings.EnableQuickHide) hidden = false;
        _looseDesktopHost?.CancelTouchFlick();
        _quickHidden = hidden;
        ApplyNativeDesktopVisibility();
        UpdateWindowVisibility();
        UpdateTrayText();
        RebuildTrayMenu();
    }

    private bool ShouldKeepNativeDesktopIconsHidden()
    {
        // Preserve the user's emergency "show Explorer icons until restart" choice
        // even if a monitor is unplugged after it was selected.
        if (_forceNativeIconsVisibleUntilRestart) return false;
        if (_state.Settings.HideNativeDesktopIcons) return true;
        return _quickHidden && _state.Settings.EnableQuickHide &&
            _state.Settings.QuickHideMode is DeckQuickHideMode.AllDesktop or DeckQuickHideMode.UngroupedIconsOnly;
    }

    private void ApplyNativeDesktopVisibility()
    {
        // The Explorer ListView is only a DISPLAY target. Never move/delete its
        // desktop items or modify Windows icon-visibility Registry settings.
        _host.SetNativeDesktopIconsVisible(!ShouldKeepNativeDesktopIconsHidden());
    }

    // A display topology change can recreate/re-show Explorer's FolderView.
    // Reassert only the intended HIDE transition, never raise/activate/reparent
    // CyanDeck windows or toggle explicitly restored native icons.
    private void RepairNativeIconVisibilityAfterStableDisplay()
    {
        try
        {
            if (_disposed || !_desktopRuntimeStarted || _safeMode) return;
            var stable = _displayTransition?.IsStableForUserLayoutAction != false;
            if (!ShouldKeepNativeDesktopIconsHidden() || !stable) return;
            var found = _host.TryGetNativeDesktopIconsVisible(out var isVisible);
            if (!NativeDesktopIconRecoveryPolicy.ShouldRehide(
                    desktopRuntimeStarted: _desktopRuntimeStarted,
                    safeMode: _safeMode,
                    shouldKeepHidden: ShouldKeepNativeDesktopIconsHidden(),
                    displayStable: stable,
                    explorerListViewVisible: found ? isVisible : null))
            {
                // The shell may not have built a new FolderView yet. Subsequent
                // one-shot probes are scheduled after topology stabilization.
                return;
            }
            _host.SetNativeDesktopIconsVisible(false);
            if (_host.TryGetNativeDesktopIconsVisible(out var afterRepair) && !afterRepair)
                DiagnosticsLog.Info("Explorer native desktop icons re-hidden after stable display transition.");
            else
                DiagnosticsLog.Warn("Explorer native desktop icon re-hide not yet confirmed; bounded retry remains scheduled.");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Stable display native icon visibility recovery failed", ex);
        }
    }

    private void StartNativeIconDisplayRepairChecks()
    {
        _nativeIconDisplayRepairTimer?.Stop();
        if (_disposed || !_desktopRuntimeStarted || _safeMode || !ShouldKeepNativeDesktopIconsHidden())
            return;
        // One immediate repair plus up to three spaced probes for late Explorer
        // rebuilds. Do NOT turn this into an always-running focus/Z-order poll.
        RepairNativeIconVisibilityAfterStableDisplay();
        _nativeIconDisplayRepairChecksRemaining = 3;
        _nativeIconDisplayRepairTimer ??= new DispatcherTimer(
            DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _nativeIconDisplayRepairTimer.Tick -= NativeIconDisplayRepairTimer_Tick;
        _nativeIconDisplayRepairTimer.Tick += NativeIconDisplayRepairTimer_Tick;
        _nativeIconDisplayRepairTimer.Start();
    }

    private void NativeIconDisplayRepairTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || --_nativeIconDisplayRepairChecksRemaining <= 0)
            _nativeIconDisplayRepairTimer?.Stop();
        RepairNativeIconVisibilityAfterStableDisplay();
    }

    private void RescueNativeExplorerIcons()
    {
        try
        {
            if (!_host.TryRestoreNativeDesktopIcons())
                throw new InvalidOperationException("Windows Explorer FolderView를 찾거나 다시 표시하지 못했습니다.");
            _forceNativeIconsVisibleUntilRestart = true;
            DiagnosticsLog.Info("Emergency Explorer native icons restored for this session; stored preferences unchanged.");
            System.Windows.MessageBox.Show(
                "Windows 기본 바탕화면 아이콘의 표시를 복구했습니다. 저장된 설정은 변경하지 않았습니다.\n" +
                "실행 중 중복 아이콘이 보일 수 있습니다. 다음 실행에서는 기존 아이콘 숨김 설정이 다시 적용됩니다.",
                "CyanDeck Explorer 아이콘 복구", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Emergency Explorer native icons restoration failed", ex);
            System.Windows.MessageBox.Show("복구에 실패했습니다. 별도 recover-desktop-icons.ps1을 실행할 수 있습니다.\n" + ex.Message,
                "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToggleNativeIcons()
    {
        _forceNativeIconsVisibleUntilRestart = false;
        _state.Settings.HideNativeDesktopIcons = !_state.Settings.HideNativeDesktopIcons;
        ApplyNativeDesktopVisibility();
        RefreshDesktopTouchPageHost();
        RefreshDesktopFolderPortalDropHost();
        RefreshLooseDesktopIconHost();
        Persist();
        RebuildTrayMenu();
    }

    private void StartDesktopDoubleClickHook()
    {
        _desktopDoubleClick = new DesktopDoubleClickHook(_dispatcher);
        _desktopDoubleClick.DoubleClicked += () => ToggleQuickHide(QuickHideInvocationSource.DesktopDoubleClick);
        _desktopDoubleClick.Start();
    }

    private DesktopMonitorTopologyOptions GetDesktopTopologyOptions() => new()
    {
        AllowInteriorSeamPageNavigation = _state.Settings.AllowInteriorMonitorSeamPageNavigation,
    };

    private static string GetMonitorTopologySurfaceId(string deviceName) => $"monitor:{deviceName}";

    private void RebuildDesktopMonitorTopology()
    {
        if (_safeMode) return;

        var metrics = new List<DesktopMonitorSurfaceMetric>();
        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            var work = screen.WorkingArea;
            metrics.Add(new DesktopMonitorSurfaceMetric(
                GetMonitorTopologySurfaceId(screen.DeviceName),
                screen.DeviceName,
                new DesktopPixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                new DesktopPixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                new DesktopPixelRect(work.Left, work.Top, work.Right, work.Bottom),
                96,
                96,
                96,
                screen.Primary));
        }

        if (_touchPageHost is not null)
        {
            metrics.AddRange(_touchPageHost.ReadTopologyMetrics());
        }

        _desktopMonitorTopology = new DesktopMonitorTopology(++_desktopTopologyRevision, metrics);
        DiagnosticsLog.Info($"Desktop Pages topology: revision={_desktopMonitorTopology.Revision}; surfaces={_desktopMonitorTopology.Surfaces.Count}; seam-navigation={_state.Settings.AllowInteriorMonitorSeamPageNavigation}");
    }

    private int? ResolveDesktopPageEdgeDirection(NativeMethods.POINT point)
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y));
        var bounds = screen.Bounds;
        DesktopMonitorHorizontalEdge? edge = null;
        int direction = 0;
        if (point.X <= bounds.Left + 3)
        {
            edge = DesktopMonitorHorizontalEdge.Left;
            direction = -1;
        }
        else if (point.X >= bounds.Right - 4)
        {
            edge = DesktopMonitorHorizontalEdge.Right;
            direction = 1;
        }

        if (edge is null) return null;

        var classification = _desktopMonitorTopology.ClassifyHorizontalEdgeAtScreenY(
            GetMonitorTopologySurfaceId(screen.DeviceName),
            edge.Value,
            point.Y,
            GetDesktopTopologyOptions());

        return classification.Decision switch
        {
            DesktopPageEdgeNavigationDecision.AllowedOuterEdge => direction,
            DesktopPageEdgeNavigationDecision.AllowedDetachedEdge => direction,
            DesktopPageEdgeNavigationDecision.AllowedInteriorSeam => direction,
            DesktopPageEdgeNavigationDecision.BlockedInteriorSeam => CountBlockedDesktopSeam(),
            _ => null,
        };
    }

    private int? CountBlockedDesktopSeam()
    {
        _desktopTopologySeamBlockedCount++;
        return null;
    }

    private bool ArePageTransitionSurfacesDpiSynchronized(int oldPage, int targetPage)
    {
        var options = GetDesktopTopologyOptions();
        foreach (var window in _windows)
        {
            if (!window.IsLoaded || window.Model.ShowOnAllPages) continue;
            if (window.Model.PageIndex != oldPage && window.Model.PageIndex != targetPage) continue;
            if (!WpfDesktopMonitorSurfaceReader.TryRead(window, $"fence:{window.Model.Id:D}", out DesktopMonitorSurfaceMetric metric)) continue;
            if (metric.HasDpiMismatch(options.DpiChangeTolerance))
            {
                _desktopTopologyDpiMismatchCount++;
                DiagnosticsLog.Info($"Desktop Pages animation skipped during DPI synchronization boundary; window-dpi={metric.WindowDpi}; wpf-dpi={metric.WpfDpiX:0.##}");
                return false;
            }
        }
        return true;
    }

    private void StartDesktopPageGestureHook()
    {
        _desktopCreationPreviewHost ??= new DesktopCreationRectangleOverlayHost(_host);
        _pageGesture = new DesktopPageGestureHook(
            _dispatcher,
            _state.Settings,
            ResolveDesktopPageEdgeDirection,
            CanCreateGroupFromDesktopGesture);
        _pageGesture.PageSwipe += SwitchPage;
        _pageGesture.CreationPreviewChanged += PreviewDesktopCreationGesture;
        _pageGesture.CreationPreviewCancelled += CancelDesktopCreationGesture;
        _pageGesture.CreationCompleted += CompleteDesktopCreationGesture;
        _pageGesture.AltDragSurfaceInteractiveChanged += active => _folderPortalDropHost?.SetInteractive(active);
        _pageGesture.Start();
    }

    private bool CanCreateGroupFromDesktopGesture() =>
        !_safeMode
        && !_compatibilityReadOnly
        && !_peek
        && !_quickHidden
        && !_desktopDisplay.IsActive
        && !_monitorContentSwapInProgress
        && IsManagedCapabilityAllowed(CreationMouseButtonIntegrationContract.ManagedCapability)
        && (_displayTransition?.IsStableForUserLayoutAction ?? true);

    private void PreviewDesktopCreationGesture(DesktopCreationRectangleGesture gesture)
        => _desktopCreationPreviewHost?.Show(gesture);

    private void CancelDesktopCreationGesture()
        => _desktopCreationPreviewHost?.Hide();

    private void CompleteDesktopCreationGesture(DesktopCreationRectangleGesture gesture)
    {
        _desktopCreationPreviewHost?.Hide();
        if (!CanCreateGroupFromDesktopGesture()) return;
        ShowDesktopCreationMenu(gesture);
    }

    private void ShowDesktopCreationMenu(DesktopCreationRectangleGesture gesture)
    {
        _desktopCreationMenu?.Close();
        _desktopCreationMenu?.Dispose();
        var menu = new Forms.ContextMenuStrip();
        _desktopCreationMenu = menu;
        var createFence = menu.Items.Add("여기에 새 그룹 만들기", null, (_, _) =>
            _dispatcher.BeginInvoke(new Action(() => CreateFenceFromDesktopRectangle(gesture))));
        createFence.Enabled = IsManagedCapabilityAllowed("Layout.CreateDeleteGroups");
        var createPortal = menu.Items.Add("여기에 Folder Portal 만들기…", null, (_, _) =>
            _dispatcher.BeginInvoke(new Action(() => CreateFolderPortalFromDesktopRectangle(gesture))));
        createPortal.Enabled = IsManagedCapabilityAllowed("Layout.CreateDeleteGroups") && IsManagedCapabilityAllowed("FolderPortal.Edit");
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("취소");
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_desktopCreationMenu, menu)) _desktopCreationMenu = null;
            menu.Dispose();
        };
        menu.Show(new System.Drawing.Point(gesture.EndScreenX, gesture.EndScreenY));
    }

    private readonly record struct DesktopCreationPlacement(
        double X, double Y, double Width, double Height, string MonitorName);

    private DesktopCreationPlacement NormalizeDesktopCreationPlacement(DesktopCreationRectangleGesture gesture)
    {
        var startPoint = new System.Drawing.Point(gesture.StartScreenX, gesture.StartScreenY);
        var screen = Forms.Screen.FromPoint(startPoint);
        var dpi = ResolveBestKnownMonitorDpi(screen.DeviceName);
        var scale = dpi is >= 48 and <= 960 ? dpi / 96d : 1d;
        var work = screen.WorkingArea;
        var workLeft = work.Left / scale;
        var workTop = work.Top / scale;
        var workRight = work.Right / scale;
        var workBottom = work.Bottom / scale;
        var rawLeftPx = Math.Clamp(Math.Min(gesture.StartScreenX, gesture.EndScreenX), screen.Bounds.Left, screen.Bounds.Right - 1);
        var rawTopPx = Math.Clamp(Math.Min(gesture.StartScreenY, gesture.EndScreenY), screen.Bounds.Top, screen.Bounds.Bottom - 1);
        var rawRightPx = Math.Clamp(Math.Max(gesture.StartScreenX, gesture.EndScreenX), screen.Bounds.Left + 1, screen.Bounds.Right);
        var rawBottomPx = Math.Clamp(Math.Max(gesture.StartScreenY, gesture.EndScreenY), screen.Bounds.Top + 1, screen.Bounds.Bottom);
        var width = Math.Clamp((rawRightPx - rawLeftPx) / scale, 220d, Math.Max(220d, work.Width / scale));
        var height = Math.Clamp((rawBottomPx - rawTopPx) / scale, 160d, Math.Max(160d, work.Height / scale));
        var x = rawLeftPx / scale;
        var y = rawTopPx / scale;
        x = Math.Clamp(x, workLeft, Math.Max(workLeft, workRight - width));
        y = Math.Clamp(y, workTop, Math.Max(workTop, workBottom - height));
        return new DesktopCreationPlacement(x, y, width, height, screen.DeviceName);
    }

    private void CreateFenceFromDesktopRectangle(DesktopCreationRectangleGesture gesture)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "빈 바탕화면에서 새 그룹 만들기")) return;
        if (!CanCreateGroupFromDesktopGesture()) return;
        var placement = NormalizeDesktopCreationPlacement(gesture);
        DeckFence? created = null;
        try
        {
            created = new DeckFence
            {
                Title = "새 그룹",
                X = placement.X,
                Y = placement.Y,
                Width = placement.Width,
                Height = placement.Height,
                ExpandedHeight = placement.Height,
                PageIndex = _state.CurrentPage,
                Tabs = [new DeckTab { Title = "항목" }]
            };
            _state.Fences.Add(created);
            CreateWindow(created);
            UpdateWindowVisibility();
            Persist();
            RebuildTrayMenu();
            DiagnosticsLog.Info($"Created group from blank-desktop rectangle; monitor={placement.MonitorName}; page={_state.CurrentPage}; button={_state.Settings.CreateGroupMouseButtonMode}");
        }
        catch (Exception ex)
        {
            if (created is not null)
            {
                var partialWindow = _windows.FirstOrDefault(window => window.Model.Id == created.Id);
                if (partialWindow is not null)
                {
                    _windows.Remove(partialWindow);
                    try { partialWindow.CloseFromController(); } catch { }
                }
                _state.Fences.Remove(created);
            }
            DiagnosticsLog.Error("Failed to create group from blank-desktop rectangle", ex);
        }
    }

    private void CreateFolderPortalFromDesktopRectangle(DesktopCreationRectangleGesture gesture)
    {
        if (!EnsureManagedCapability("Layout.CreateDeleteGroups", "빈 바탕화면에서 Folder Portal 만들기")) return;
        if (!EnsureManagedCapability("FolderPortal.Edit", "빈 바탕화면에서 Folder Portal 만들기")) return;
        if (!CanCreateGroupFromDesktopGesture()) return;
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Folder Portal로 표시할 폴더를 선택하세요",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        if (!AltDragFolderPortalPolicy.TryNormalizeCandidatePath(dialog.SelectedPath, out var fullPath)) return;
        var placement = NormalizeDesktopCreationPlacement(gesture);
        CreateOrReuseFolderPortal(fullPath, placement, "blank-desktop rectangle");
    }

    private void StartDesktopTouchPageHost()
    {
        _touchPageHost ??= new DesktopPageTouchSurfaceHost(
            _host,
            GetTouchPageOptions,
            CanNavigateTouchPage,
            PreviewTouchPagePull,
            CancelTouchPagePullPreview,
            CommitTouchPageSwipe);
        RefreshDesktopTouchPageHost();
    }

    private void StartDesktopFolderPortalDropHost()
    {
        _folderPortalDropHost ??= new DesktopFolderPortalDropSurfaceHost(
            _host,
            () => !_safeMode && !_compatibilityReadOnly && _state.Settings.HideNativeDesktopIcons,
            () => _safeMode,
            CanCreateFolderPortalFromDesktopAltDrop,
            CreateFolderPortalFromDesktopAltDrop);
        RefreshDesktopFolderPortalDropHost();
    }

    private void RefreshDesktopFolderPortalDropHost()
    {
        var enabled = !_safeMode
            && !_compatibilityReadOnly
            && _state.Settings.HideNativeDesktopIcons
            && !_peek
            && !_desktopDisplay.IsActive;
        _folderPortalDropHost?.Refresh(enabled);
    }

    private bool CanCreateFolderPortalFromDesktopAltDrop() =>
        !_safeMode
        && !_compatibilityReadOnly
        && !_peek
        && !_desktopDisplay.IsActive
        && _state.Settings.HideNativeDesktopIcons
        && IsManagedCapabilityAllowed("FolderPortal.Edit")
        && IsManagedCapabilityAllowed("Layout.CreateDeleteGroups")
        && IsManagedCapabilityAllowed("FolderPortal.CreateFromDesktopAltDrop")
        && (_displayTransition?.IsStableForUserLayoutAction ?? true);

    private AltDragFolderPortalCreateResult CreateFolderPortalFromDesktopAltDrop(AltDragFolderPortalCreateRequest request)
    {
        if (!CanCreateFolderPortalFromDesktopAltDrop())
            return AltDragFolderPortalCreateResult.Rejected;
        if (!AltDragFolderPortalPolicy.TryNormalizeCandidatePath(request.FolderPath, out var fullPath))
            return AltDragFolderPortalCreateResult.Rejected;

        var screenPoint = new System.Drawing.Point(
            (int)Math.Round(request.ScreenPoint.X),
            (int)Math.Round(request.ScreenPoint.Y));
        var screen = Forms.Screen.FromPoint(screenPoint);
        var dpi = ResolveBestKnownMonitorDpi(screen.DeviceName);
        var scale = dpi is >= 48 and <= 960 ? dpi / 96d : 1d;
        var work = screen.WorkingArea;
        var workLeft = work.Left / scale;
        var workTop = work.Top / scale;
        var workRight = work.Right / scale;
        var workBottom = work.Bottom / scale;
        var width = Math.Min(360d, Math.Max(260d, work.Width / scale));
        var height = Math.Min(310d, Math.Max(180d, work.Height / scale));
        var x = request.ScreenPoint.X / scale - 24d;
        var y = request.ScreenPoint.Y / scale - 18d;
        x = Math.Clamp(x, workLeft, Math.Max(workLeft, workRight - width));
        y = Math.Clamp(y, workTop, Math.Max(workTop, workBottom - height));
        var placement = new DesktopCreationPlacement(x, y, width, height, screen.DeviceName);
        return CreateOrReuseFolderPortal(fullPath, placement, "blank-desktop Alt-drop");
    }

    private AltDragFolderPortalCreateResult CreateOrReuseFolderPortal(
        string fullPath,
        DesktopCreationPlacement placement,
        string diagnosticSource)
    {
        var existing = _state.Fences
            .SelectMany(fence => fence.Tabs.Select((tab, index) => new { Fence = fence, Tab = tab, Index = index }))
            .FirstOrDefault(x => x.Tab.Kind == DeckTabKind.FolderPortal
                && FolderPortalPathEquals(x.Tab.PortalPath, fullPath));

        if (existing is not null)
        {
            existing.Fence.SelectedTabIndex = existing.Index;
            if (!existing.Fence.ShowOnAllPages)
                _state.CurrentPage = Math.Clamp(existing.Fence.PageIndex, 0, GetMaxPage());
            var existingWindow = _windows.FirstOrDefault(window => window.Model.Id == existing.Fence.Id);
            existingWindow?.RefreshFromModel();
            UpdateWindowVisibility();
            Persist();
            return AltDragFolderPortalCreateResult.ReusedExisting;
        }

        DeckFence? createdFence = null;
        try
        {
            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var title = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(title)) title = fullPath;

            var portalTab = new DeckTab
            {
                Title = title,
                Kind = DeckTabKind.FolderPortal,
                PortalPath = fullPath,
                PortalRootPath = fullPath
            };
            createdFence = new DeckFence
            {
                Title = title,
                X = placement.X,
                Y = placement.Y,
                Width = placement.Width,
                Height = placement.Height,
                ExpandedHeight = placement.Height,
                PageIndex = _state.CurrentPage,
                Tabs = [portalTab]
            };

            _state.Fences.Add(createdFence);
            CreateWindow(createdFence);
            UpdateWindowVisibility();
            Persist();
            RebuildTrayMenu();
            DiagnosticsLog.Info($"Created Folder Portal from {diagnosticSource}; monitor={placement.MonitorName}; page={_state.CurrentPage}");
            return AltDragFolderPortalCreateResult.Created;
        }
        catch (Exception ex)
        {
            if (createdFence is not null)
            {
                var partialWindow = _windows.FirstOrDefault(window => window.Model.Id == createdFence.Id);
                if (partialWindow is not null)
                {
                    _windows.Remove(partialWindow);
                    try { partialWindow.CloseFromController(); } catch { }
                }
                _state.Fences.Remove(createdFence);
            }
            DiagnosticsLog.Error($"Failed to create Folder Portal from {diagnosticSource}", ex);
            return AltDragFolderPortalCreateResult.Failed;
        }
    }

    private static bool FolderPortalPathEquals(string? candidate, string normalizedPath)
    {
        if (!AltDragFolderPortalPolicy.TryNormalizeCandidatePath(candidate, out var normalizedCandidate))
            return false;
        return string.Equals(normalizedCandidate, normalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    private DesktopPageTouchSwipeOptions GetTouchPageOptions() => new()
    {
        Enabled = _state.Settings.EnableTouchPageGestures
            && _state.Settings.HideNativeDesktopIcons
            && !_safeMode
            && !_peek
            && !_desktopDisplay.IsActive,
        TwoFingerEnabled = true,
        OneFingerEdgeEnabled = false,
    };

    private bool CanNavigateTouchPage(DesktopPageTouchSwipeDirection direction) => direction switch
    {
        DesktopPageTouchSwipeDirection.Previous => _state.CurrentPage > 0,
        DesktopPageTouchSwipeDirection.Next => _state.CurrentPage < GetMaxPage(),
        _ => false,
    };

    private void PreviewTouchPagePull(string surfaceId, DesktopPageTouchSwipeDirection direction, double progress)
    {
        if (!CanNavigateTouchPage(direction))
        {
            CancelTouchPagePullPreview(surfaceId);
            return;
        }

        if (!string.Equals(_touchPageTopologySurfaceId, surfaceId, StringComparison.Ordinal) || progress <= 0d)
        {
            _touchPageTopologySurfaceId = surfaceId;
            _touchPageTopologyToken = _desktopMonitorTopology.CreateToken(surfaceId);
        }

        progress = Math.Clamp(progress, 0d, 1d);
        _touchPagePreviewDirection = direction;
        _touchPagePreviewProgress = progress * 0.14d;
        var pageDirection = direction == DesktopPageTouchSwipeDirection.Previous ? -1 : 1;
        foreach (var window in _windows)
        {
            if (!window.IsVisible || window.Model.ShowOnAllPages || window.Model.PageIndex != _state.CurrentPage) continue;
            window.PreviewPagePull(pageDirection, progress);
        }
    }

    private void CancelTouchPagePullPreview() => CancelTouchPagePullPreview(null);

    private void CancelTouchPagePullPreview(string? surfaceId)
    {
        var direction = _touchPagePreviewDirection;
        var progress = _touchPagePreviewProgress;
        var stability = EvaluateTouchTopologyStability(surfaceId);
        ClearTouchTopologyGesture();
        _touchPagePreviewDirection = null;
        _touchPagePreviewProgress = 0;

        if (stability != DesktopPageTopologyStability.Stable)
        {
            if (stability == DesktopPageTopologyStability.Replan) _desktopTopologyReplanCount++;
            CancelTouchPreviewImmediately();
            return;
        }

        if (direction is null || progress <= 0 || !_state.Settings.AnimatePageTransitions)
        {
            CancelTouchPreviewImmediately();
            return;
        }

        var pageDirection = direction == DesktopPageTouchSwipeDirection.Previous
            ? DesktopPageTransitionDirection.Previous
            : DesktopPageTransitionDirection.Next;
        var options = GetPageTransitionOptions();
        var systemAnimationsAllowed = WpfDesktopPageTransitionAnimationFactory.AreSystemAnimationsAllowed();
        foreach (var window in _windows)
        {
            if (!window.IsLoaded || window.Model.ShowOnAllPages || window.Model.PageIndex != _state.CurrentPage) continue;
            var plan = DesktopPageTransitionPlanner.Create(
                new DesktopPageTransitionRequest(
                    pageDirection,
                    DesktopPageTransitionPhase.Cancel,
                    window.GetPageTransitionSurfaceWidthDip(),
                    progress,
                    systemAnimationsAllowed),
                options);
            window.AnimatePageCancel(plan);
        }
    }

    private void CommitTouchPageSwipe(string surfaceId, DesktopPageTouchSwipeDirection direction)
    {
        var progress = _touchPagePreviewDirection == direction ? _touchPagePreviewProgress : 0d;
        var stability = EvaluateTouchTopologyStability(surfaceId);
        bool dpiSynchronized = _desktopMonitorTopology.IsSurfaceDpiSynchronized(surfaceId, GetDesktopTopologyOptions());
        ClearTouchTopologyGesture();
        _touchPagePreviewDirection = null;
        _touchPagePreviewProgress = 0;

        if (stability == DesktopPageTopologyStability.CancelSurfaceMissing)
        {
            CancelTouchPreviewImmediately();
            return;
        }

        if (stability == DesktopPageTopologyStability.Replan || !dpiSynchronized)
        {
            if (stability == DesktopPageTopologyStability.Replan) _desktopTopologyReplanCount++;
            if (!dpiSynchronized) _desktopTopologyDpiMismatchCount++;
            CancelTouchPreviewImmediately();
            progress = 0d;
        }

        SwitchPage(direction == DesktopPageTouchSwipeDirection.Previous ? -1 : 1, progress);
    }

    private DesktopPageTopologyStability EvaluateTouchTopologyStability(string? surfaceId)
    {
        if (string.IsNullOrWhiteSpace(surfaceId) ||
            string.IsNullOrWhiteSpace(_touchPageTopologySurfaceId) ||
            !string.Equals(surfaceId, _touchPageTopologySurfaceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(_touchPageTopologyToken.SurfaceId))
        {
            return DesktopPageTopologyStability.Stable;
        }

        return _desktopMonitorTopology.EvaluateStability(_touchPageTopologyToken, GetDesktopTopologyOptions());
    }

    private void ClearTouchTopologyGesture()
    {
        _touchPageTopologySurfaceId = null;
        _touchPageTopologyToken = default;
    }

    private void CancelTouchPreviewImmediately()
    {
        foreach (var window in _windows)
        {
            if (window.IsLoaded) window.CancelPagePullPreview();
        }
    }

    private void RefreshDesktopTouchPageHost()
    {
        _touchPageHost?.Refresh(_state.Settings.EnableTouchPageGestures && _state.Settings.HideNativeDesktopIcons && !_peek && !_desktopDisplay.IsActive);
        RebuildDesktopMonitorTopology();
    }

    private void StartLooseDesktopIconHost()
    {
        _looseQuickHideCoordinator ??= new LooseIconQuickHideExclusionCoordinator(
            ReadLooseQuickHideSnapshot,
            PersistLooseQuickHideMutationsAsync,
            RefreshLooseDesktopIconHost);
        _looseDesktopHost ??= new LooseDesktopIconHost(
            _host,
            _icons,
            Persist,
            AutoClassifyLooseItems,
            SetLooseQuickHideExcludedAsync,
            GetTouchIconFlickOptions,
            GetLooseDesktopFlickDestinations,
            CanFlickLooseItemToCollection,
            CommitFlickLooseItemToCollection,
            PreviewLooseDesktopFlickTarget,
            () => new ShortcutBadgeOptions
            {
                HideFileShortcutBadges = _state.Settings.HideFileShortcutBadges,
                HideInternetShortcutBadges = _state.Settings.HideInternetShortcutBadges,
                IncludeFolderPortalItems = _state.Settings.ShowShortcutBadgesInFolderPortals,
            },
            () => new LooseIconOpacityOptions(
                _state.Settings.StandaloneIconChameleonEnabled,
                _state.Settings.StandaloneIconIdleOpacity,
                _state.Settings.StandaloneIconHoverOpacity,
                _state.Settings.ApplyStandaloneIconChameleonToRecycleBin,
                _state.Settings.EnableChameleon
                    ? Math.Clamp(_state.Settings.ChameleonIdleOpacity, 0.15, 1d)
                    : 1d));
        RefreshLooseDesktopIconHost();
    }

    private void RefreshLooseDesktopIconHost()
    {
        if (_safeMode)
        {
            _looseDesktopHost?.Refresh([], false, false, false, DeckQuickHideMode.AllDesktop);
            return;
        }

        _recycleBinDesktopItem ??= DesktopSystemItems.CreateRecycleBin(
            _state.Settings.RecycleBinPositionX,
            _state.Settings.RecycleBinPositionY);

        // Explorer's desktop ListView is hidden as a whole, so its native Recycle Bin cannot
        // remain independently visible. Supply a CyanDeck-owned Shell surrogate alongside
        // ordinary loose items. It stays on the desktop but is never shown over normal apps.
        var desktopItems = new List<DeckItem>(_state.LooseDesktopItems.Count + 1);
        desktopItems.AddRange(_state.LooseDesktopItems);
        // The standalone Recycle Bin is independent of system-icon groups and
        // continues to exist even when Explorer's native desktop icons are hidden.
        if (_state.Settings.ShowStandaloneRecycleBin)
            desktopItems.Add(_recycleBinDesktopItem);

        var enabled = _state.Settings.HideNativeDesktopIcons
            && !_peek
            && !_desktopDisplay.IsActive;
        _looseDesktopHost?.Refresh(
            desktopItems,
            enabled,
            _state.Settings.EnableQuickHide,
            _quickHidden,
            _state.Settings.QuickHideMode);
    }

    private LooseDesktopItemQuickHideSnapshot? ReadLooseQuickHideSnapshot(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id)) return null;
        var item = _state.LooseDesktopItems.FirstOrDefault(i => i.Id == id);
        return item is null
            ? null
            : new LooseDesktopItemQuickHideSnapshot(
                item.Id.ToString("D"),
                QuickHideLooseIconSurfaceKind.CyanDeckManagedLooseIcon,
                IsLooseDesktopItem: true,
                IsGroupMember: false,
                item.ExcludeFromQuickHide);
    }

    private Task<bool> PersistLooseQuickHideMutationsAsync(
        IReadOnlyList<LooseIconQuickHideMutation> mutations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = new Dictionary<Guid, bool>();
        foreach (var mutation in mutations)
        {
            if (!Guid.TryParse(mutation.ItemId, out var id)) continue;
            var item = _state.LooseDesktopItems.FirstOrDefault(i => i.Id == id);
            if (item is null) continue;
            before[id] = item.ExcludeFromQuickHide;
            item.ExcludeFromQuickHide = mutation.ExcludeFromQuickHide;
        }
        if (before.Count == 0) return Task.FromResult(false);

        try
        {
            _persistDirty = true;
            PersistNow(force: true, requireDurableSave: true);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            foreach (var pair in before)
            {
                var item = _state.LooseDesktopItems.FirstOrDefault(i => i.Id == pair.Key);
                if (item is not null) item.ExcludeFromQuickHide = pair.Value;
            }
            DiagnosticsLog.Error("Failed to persist loose-icon Quick-hide exclusion", ex);
            return Task.FromResult(false);
        }
    }

    private async Task SetLooseQuickHideExcludedAsync(IReadOnlyList<Guid> ids, bool excluded)
    {
        if (_looseQuickHideCoordinator is null) return;
        var result = await _looseQuickHideCoordinator.SetExcludedAsync(ids.Select(id => id.ToString("D")), excluded);
        if (result.Status == LooseIconExclusionEditStatus.PersistFailed)
            System.Windows.MessageBox.Show("빠른 숨김 제외 설정을 저장하지 못했습니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private DesktopIconFlickOptions GetTouchIconFlickOptions()
    {
        var enabled = !_safeMode
            && !_compatibilityReadOnly
            && _state.Settings.EnableTouchIconFlick
            && _state.Settings.HideNativeDesktopIcons
            && _state.LooseDesktopItems.Count > 0
            && !_peek
            && !_desktopDisplay.IsActive
            && (_displayTransition?.IsStableForUserLayoutAction ?? true)
            && IsManagedCapabilityAllowed("Input.TouchIconFlick");
        return new DesktopIconFlickOptions(Enabled: enabled);
    }

    private IReadOnlyList<LooseDesktopFlickDestination> GetLooseDesktopFlickDestinations()
    {
        if (!_state.Settings.EnableTouchIconFlick || _peek || _desktopDisplay.IsActive)
            return Array.Empty<LooseDesktopFlickDestination>();

        var targets = new List<LooseDesktopFlickDestination>();
        foreach (var window in _windows)
        {
            if (!window.IsVisible || !window.IsLoaded) continue;
            var fence = window.Model;
            if (!fence.ShowOnAllPages && fence.PageIndex != _state.CurrentPage) continue;
            if (fence.Tabs.Count == 0) continue;
            var index = Math.Clamp(fence.SelectedTabIndex, 0, fence.Tabs.Count - 1);
            var tab = fence.Tabs[index];
            if (tab.Kind != DeckTabKind.Collection) continue;
            if (!window.TryGetPhysicalBounds(out var bounds) || bounds.Width <= 0 || bounds.Height <= 0) continue;
            targets.Add(new LooseDesktopFlickDestination(fence.Id, tab.Id, bounds));
        }
        return targets;
    }

    private bool CanFlickLooseItemToCollection(Guid itemId, LooseDesktopFlickDestination destination)
    {
        if (!GetTouchIconFlickOptions().Enabled) return false;
        var item = _state.LooseDesktopItems.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return false;
        var fence = _state.Fences.FirstOrDefault(f => f.Id == destination.FenceId);
        if (fence is null || fence.Tabs.Count == 0) return false;
        var selectedIndex = Math.Clamp(fence.SelectedTabIndex, 0, fence.Tabs.Count - 1);
        var selected = fence.Tabs[selectedIndex];
        if (selected.Id != destination.TabId || selected.Kind != DeckTabKind.Collection) return false;
        return _windows.Any(w => w.Model.Id == fence.Id && w.IsVisible);
    }

    private void CommitFlickLooseItemToCollection(Guid itemId, LooseDesktopFlickDestination destination)
    {
        if (!CanFlickLooseItemToCollection(itemId, destination)) return;
        if (!MoveLooseDesktopItemToCollection(itemId, destination.FenceId, destination.TabId)) return;
        PreviewLooseDesktopFlickTarget(null);
    }

    private void PreviewLooseDesktopFlickTarget(LooseDesktopFlickDestination? destination)
    {
        foreach (var window in _windows)
        {
            var active = destination is LooseDesktopFlickDestination target
                && window.Model.Id == target.FenceId
                && window.Model.Tabs.Count > 0
                && window.Model.Tabs[Math.Clamp(window.Model.SelectedTabIndex, 0, window.Model.Tabs.Count - 1)].Id == target.TabId;
            window.SetTouchFlickTargetPreview(active);
        }
    }

    private void OnItemsDetachToDesktopRequested(FenceWindow source, Guid tabId, IReadOnlyList<Guid> itemIds, System.Windows.Point screenAnchor)
    {
        if (itemIds.Count == 0) return;
        var tab = source.Model.Tabs.FirstOrDefault(t => t.Id == tabId && t.Kind == DeckTabKind.Collection);
        if (tab is null) return;

        var ids = itemIds.Where(id => id != Guid.Empty).Distinct().ToHashSet();
        var items = tab.Items.Where(i => ids.Contains(i.Id)).ToArray();
        if (items.Length == 0) return;

        var dpi = VisualTreeHelper.GetDpi(source);
        var scaleX = double.IsFinite(dpi.DpiScaleX) && dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1d;
        var scaleY = double.IsFinite(dpi.DpiScaleY) && dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1d;
        var cellW = LooseDesktopIconLayoutProfile.CellWidthDip * scaleX;
        var cellH = LooseDesktopIconLayoutProfile.CellHeightDip * scaleY;
        var itemW = LooseDesktopIconLayoutProfile.ItemWidthDip * scaleX;
        var itemH = LooseDesktopIconLayoutProfile.ItemHeightDip * scaleY;
        var columns = Math.Max(1, Math.Min(4, items.Length));

        // Put detached icons beside the group, not underneath it. Fence windows live above
        // the desktop icon child surface, so dropping the new loose icon at the click point
        // would make it appear to have vanished until the group was moved away.
        var groupTopLeft = source.PointToScreen(new System.Windows.Point(0, 0));
        var groupBottomRight = source.PointToScreen(new System.Windows.Point(source.ActualWidth, source.ActualHeight));
        var workArea = Forms.Screen.FromPoint(new System.Drawing.Point((int)Math.Round(screenAnchor.X), (int)Math.Round(screenAnchor.Y))).WorkingArea;
        var insetX = LooseDesktopIconLayoutProfile.EdgeInsetDip * scaleX;
        var rightX = groupBottomRight.X + insetX;
        var leftX = groupTopLeft.X - itemW - insetX;
        var placeRight = rightX + itemW <= workArea.Right || leftX < workArea.Left;
        var baseX = placeRight ? rightX : leftX;
        var xDirection = placeRight ? 1d : -1d;
        var baseY = Math.Clamp(screenAnchor.Y - itemH / 2d, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - itemH));

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            tab.Items.Remove(item);

            // A manually detached item is a deliberate desktop placement. Do not let the
            // background rules immediately pull it back into a group.
            item.PinnedToDesktop = true;
            var column = index % columns;
            var row = index / columns;
            item.PositionX = baseX + xDirection * column * cellW;
            item.PositionY = baseY + row * cellH;

            var normalized = DesktopDirectoryService.Normalize(item.Path);
            _state.LooseDesktopItems.RemoveAll(existing =>
                existing.Id == item.Id
                || string.Equals(DesktopDirectoryService.Normalize(existing.Path), normalized, StringComparison.OrdinalIgnoreCase));
            _state.LooseDesktopItems.Add(item);
        }

        SyncLegacyUngroupedPaths();
        source.RefreshItems();
        RefreshLooseDesktopIconHost();
        Persist();
    }

    private bool MoveLooseDesktopItemToCollection(Guid itemId, Guid fenceId, Guid tabId)
    {
        var item = _state.LooseDesktopItems.FirstOrDefault(i => i.Id == itemId);
        var fence = _state.Fences.FirstOrDefault(f => f.Id == fenceId);
        var tab = fence?.Tabs.FirstOrDefault(t => t.Id == tabId && t.Kind == DeckTabKind.Collection);
        if (item is null || tab is null) return false;

        _state.LooseDesktopItems.Remove(item);
        var normalized = DesktopDirectoryService.Normalize(item.Path);
        foreach (var other in _state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection))
        {
            other.Items.RemoveAll(i => i.Id != item.Id &&
                string.Equals(DesktopDirectoryService.Normalize(i.Path), normalized, StringComparison.OrdinalIgnoreCase));
        }

        item.PinnedToDesktop = false;
        item.PositionX = null;
        item.PositionY = null;
        if (!tab.Items.Any(i => i.Id == item.Id))
            tab.Items.Add(item);

        SyncLegacyUngroupedPaths();
        foreach (var window in _windows) window.RefreshItems();
        RefreshLooseDesktopIconHost();
        Persist();
        return true;
    }

    private void AutoClassifyLooseItems(IReadOnlyList<Guid> ids)
    {
        var changed = false;
        foreach (var id in ids.Distinct())
        {
            var item = _state.LooseDesktopItems.FirstOrDefault(i => i.Id == id);
            if (item is null) continue;
            var tab = RuleEngine.PickTab(_state, item.Path, includeRules: true);
            if (tab is null) continue;
            _state.LooseDesktopItems.Remove(item);
            item.PinnedToDesktop = false;
            item.PositionX = null;
            item.PositionY = null;
            if (!tab.Items.Any(i => string.Equals(DesktopDirectoryService.Normalize(i.Path), DesktopDirectoryService.Normalize(item.Path), StringComparison.OrdinalIgnoreCase)))
                tab.Items.Add(item);
            changed = true;
        }
        if (!changed) return;
        SyncLegacyUngroupedPaths();
        foreach (var window in _windows) window.RefreshItems();
        RefreshLooseDesktopIconHost();
        Persist();
    }

    private void StartDesktopWatchers() => RefreshDesktopWatchers(force: true, reconcile: false);

    private void RefreshDesktopWatchers(bool force = false, bool reconcile = true)
    {
        var roots = DesktopDirectoryService.GetRoots();
        if (!force && _desktopWatchers.Matches(roots)) return;

        var previousRoots = _desktopWatchers.GetRoots();
        if (!_desktopWatchers.Rebind(roots, dir =>
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    InternalBufferSize = 32 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
                };
                void QueueIfCurrent(Action action) => _dispatcher.BeginInvoke(() =>
                {
                    if (!_desktopWatchers.Owns(watcher)) return;
                    action();
                });
                FileSystemEventHandler created = (_, e) => QueueIfCurrent(() =>
                {
                    _icons.Invalidate(e.FullPath);
                    MarkNewDesktopItemUngrouped(e.FullPath);
                    if (_state.Settings.AutoImportDesktop)
                        ScheduleDesktopReconcile();
                    else
                        _desktopFirstSeenGate?.MarkKnown(DesktopDirectoryService.Normalize(e.FullPath));
                });
                FileSystemEventHandler changed = (_, e) => QueueIfCurrent(() =>
                {
                    _icons.Invalidate(e.FullPath);
                    _looseDesktopHost?.RefreshIcon(e.FullPath);
                    if (_state.Settings.AutoImportDesktop) ScheduleDesktopReconcile();
                });
                FileSystemEventHandler deleted = (_, e) => QueueIfCurrent(() =>
                {
                    _icons.Invalidate(e.FullPath);
                    _desktopFirstSeenGate?.Forget(DesktopDirectoryService.Normalize(e.FullPath));
                    RemoveUngroupedDesktopMarker(e.FullPath);
                    if (_state.Settings.AutoImportDesktop) ScheduleDesktopReconcile();
                });
                RenamedEventHandler renamed = (_, e) => QueueIfCurrent(() =>
                {
                    _icons.Invalidate(e.OldFullPath);
                    _icons.Invalidate(e.FullPath);
                    _desktopFirstSeenGate?.RenameKnown(DesktopDirectoryService.Normalize(e.OldFullPath), DesktopDirectoryService.Normalize(e.FullPath));
                    RenameUngroupedDesktopMarker(e.OldFullPath, e.FullPath);
                    if (_state.Settings.AutoImportDesktop) ScheduleDesktopReconcile();
                });
                watcher.Created += created;
                watcher.Deleted += deleted;
                watcher.Changed += changed;
                watcher.Renamed += renamed;
                watcher.Error += (_, e) => QueueIfCurrent(() =>
                {
                    DiagnosticsLog.Error($"Desktop watcher overflow/error for {dir}", e.GetException());
                    RefreshDesktopWatchers(force: true, reconcile: true);
                });
                // DesktopWatcherLifetime enables the entire staged watcher set
                // after all callbacks are subscribed and all roots are valid.
                return watcher;
            }
            catch (Exception ex)
            {
                // A failed EnableRaisingEvents can leave an allocated native
                // watcher that never entered the lifetime owner's collection.
                watcher?.Dispose();
                DiagnosticsLog.Error($"Failed to watch desktop directory {dir}", ex);
                return null;
            }
        }))
        {
            // Do not migrate stored paths when the replacement watcher set failed.
            // The old watchers are still active; the next periodic refresh retries.
            return;
        }

        if (previousRoots.Length > 0 && !new HashSet<string>(previousRoots, StringComparer.OrdinalIgnoreCase).SetEquals(roots))
        {
            MigrateDesktopRootReferences(previousRoots, roots);
            ResetDesktopFirstSeenGate();
            DiagnosticsLog.Info($"Desktop directory changed; watcher roots rebound ({previousRoots.Length} -> {roots.Count})");
        }
        if (reconcile && _state.Settings.AutoImportDesktop) ScheduleDesktopReconcile(220);
    }

    private void ScheduleDesktopReconcile(int delayMs = 350)
    {
        _desktopReconcileScheduler ??= new DesktopReconcileScheduler(_dispatcher, () =>
        {
            if (!_disposed && _desktopRuntimeStarted && _state.Settings.AutoImportDesktop)
                ReorganizeDesktop();
        });
        _desktopReconcileScheduler.Request(delayMs);
    }

    private void MigrateDesktopRootReferences(IReadOnlyList<string> oldRoots, IReadOnlyList<string> newRoots)
    {
        var changed = false;
        foreach (var tab in _state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection))
        {
            for (var i = tab.Items.Count - 1; i >= 0; i--)
            {
                var item = tab.Items[i];
                if (File.Exists(item.Path) || Directory.Exists(item.Path)) continue;
                var oldRoot = oldRoots.FirstOrDefault(root => DesktopDirectoryService.IsDirectChild(item.Path, root));
                if (oldRoot is null) continue;

                var name = Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var replacement = newRoots
                    .Select(root => Path.Combine(root, name))
                    .FirstOrDefault(candidate => File.Exists(candidate) || Directory.Exists(candidate));
                if (!string.IsNullOrWhiteSpace(replacement))
                {
                    item.Path = DesktopDirectoryService.Normalize(replacement);
                    changed = true;
                }
                else
                {
                    tab.Items.RemoveAt(i);
                    changed = true;
                }
            }
        }

        foreach (var item in _state.LooseDesktopItems.ToArray())
        {
            if (File.Exists(item.Path) || Directory.Exists(item.Path)) continue;
            var oldRoot = oldRoots.FirstOrDefault(root => DesktopDirectoryService.IsDirectChild(item.Path, root));
            if (oldRoot is null) continue;
            var name = Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var replacement = newRoots
                .Select(root => Path.Combine(root, name))
                .FirstOrDefault(candidate => File.Exists(candidate) || Directory.Exists(candidate));
            if (!string.IsNullOrWhiteSpace(replacement))
            {
                item.Path = DesktopDirectoryService.Normalize(replacement);
                changed = true;
            }
            else
            {
                _state.LooseDesktopItems.Remove(item);
                changed = true;
            }
        }

        if (changed)
        {
            SyncLegacyUngroupedPaths();
            foreach (var window in _windows) window.RefreshItems();
            RefreshLooseDesktopIconHost();
            Persist();
        }
    }

    private void StartShellRecovery()
    {
        // The scheduler owns the DispatcherTimer and unsubscribes when the runtime stops.
        // Repair semantics (idle/menu gating and Explorer identity) stay in this controller.
        _shellRecoveryScheduler?.Dispose();
        _shellRecoveryScheduler = new DesktopShellRecoveryScheduler(
            _dispatcher, TimeSpan.FromSeconds(4), () =>
        {
            // The shell-health timer must be observational during steady state. Rewriting top-level
            // HWND styles/Z-order on every tick can flash layered WPF windows, dismiss popup menus,
            // and can look like foreground activity to Windows while the user is away.
            //
            // GetLastInputInfo is intentionally independent of foreground-window changes. Once the
            // user has been idle for a few seconds, suspend all visual/window recovery until genuine
            // keyboard/mouse input resumes. Background file watching/automation can continue.
            var idleFor = GetUserIdleDuration();
            var suppressVisualRecovery = idleFor >= TimeSpan.FromSeconds(3);
            if (suppressVisualRecovery != _shellRecoveryIdleSuppressed)
            {
                _shellRecoveryIdleSuppressed = suppressVisualRecovery;
                DiagnosticsLog.Info(suppressVisualRecovery
                    ? "Shell visual recovery suspended while user is idle"
                    : "Shell visual recovery resumed after user input");
            }

            var transientMenuOpen = _trayMenuVisible || FenceWindow.IsAnyContextMenuOpen;
            if (!suppressVisualRecovery && !_peek && !transientMenuOpen)
            {
                var shellChanged = false;
                var previousShellWasValid = true;
                try { shellChanged = _host.RefreshDesktopShellIdentity(out previousShellWasValid); } catch { }

                if (shellChanged)
                {
                    // Explorer may transiently expose a different WorkerW/SHELLDLL_DefView pair while
                    // virtual desktops, wallpaper, shell surfaces, or focus state are changing. If the
                    // previous pair is still a valid HWND pair, treating that transient identity change
                    // as an Explorer restart would restack every transparent Fence and produce a flash.
                    // Only restack Fence HWNDs after the old Explorer desktop host was actually destroyed.
                    if (!previousShellWasValid)
                    {
                        foreach (var window in _windows.ToArray())
                        {
                            try { _host.EnsureAttached(window); } catch { }
                        }
                        _calendar?.NotifyExplorerShellRebuilt();
                        DiagnosticsLog.Info("Explorer desktop host was rebuilt; Fence + Calendar desktop-band placement attempted once");
                    }
                    else
                    {
                        DiagnosticsLog.Info("Explorer desktop identity changed transiently; Fence HWNDs left untouched");
                    }

                    // These helper surfaces are true Explorer children and must follow a new DefView.
                    // They are not visible Fence content and can be reattached independently.
                    try { _touchPageHost?.EnsureAttached(); } catch { }
                    try { _folderPortalDropHost?.EnsureAttached(); } catch { }
                    try { _desktopCreationPreviewHost?.EnsureAttached(); } catch { }
                    try { _looseDesktopHost?.EnsureAttached(); } catch { }
                }

                // Do not periodically Show/Hide, restyle, reparent, or restack Fence windows. Window
                // visibility and attachment are maintained by their owning state transitions. A steady
                // four-second repair loop is more harmful for layered WPF surfaces than a rare stale
                // state, which remains recoverable through explicit visibility recovery.
                // Also retry a calendar that was intentionally hidden because the
                // Explorer desktop was unavailable at startup.
                // v21: Never poll/retry a hidden calendar from the global shell timer.
                // Only an explicit user action or genuine Explorer handle destruction may retry.
                try { _peekTaskbarButton?.EnsureVisible(); } catch { }
                if (_forceNativeIconsVisibleUntilRestart)
                {
                    try { _host.SetNativeDesktopIconsVisible(true); } catch { }
                }
                else if (_state.Settings.HideNativeDesktopIcons)
                {
                    try { _host.SetNativeDesktopIconsVisible(false); } catch { }
                }
            }

            if (_state.Settings.TrackDesktopLocationChanges)
            {
                try { RefreshDesktopWatchers(); } catch (Exception ex) { DiagnosticsLog.Error("Desktop directory refresh failed", ex); }
            }
            if (_state.Settings.AutoImportDesktop && DateTime.UtcNow - _lastAutomationMaintenanceUtc > TimeSpan.FromMinutes(10))
                ScheduleDesktopReconcile(700);
            if (!suppressVisualRecovery &&
                (_quickHideStartupStatus is QuickHideStartupApplyStatus.DeferredDesktopNotReady or QuickHideStartupApplyStatus.FailedRetryable))
                _ = TryApplyStartupQuickHideNormalizationAsync();
        }, "CyanDeck shell recovery");
        _shellRecoveryScheduler.Start();
    }

    private static TimeSpan GetUserIdleDuration()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };
        if (!NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var now = unchecked((uint)Environment.TickCount);
        var elapsed = unchecked(now - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    private void StartDisplayTransitionStabilizer()
    {
        _displayTransition?.Dispose();
        _displayTransition = new WpfDisplayTransitionBridge(
            _dispatcher,
            CaptureDisplayTopologySnapshot,
            ScheduleDisplayTransitionCheck,
            RestoreStableDisplayLayout,
            FlushDeferredDisplayLayoutSave,
            CancelTransientDisplayInteractions,
            message => DiagnosticsLog.Info(message));
    }

    private DisplayTopologySnapshot CaptureDisplayTopologySnapshot()
    {
        var monitors = MonitorConfigurationService.CaptureMonitors();
        if (monitors.Count == 0) return DisplayTopologySnapshot.Invalid;
        var fingerprint = MonitorConfigurationService.GetSignature(monitors);
        return new DisplayTopologySnapshot(
            fingerprint,
            monitors.Count,
            SystemParameters.IsRemoteSession,
            !string.IsNullOrWhiteSpace(fingerprint));
    }

    private void OnFenceDisplayTransitionMessage(int message)
    {
        // Moving a CyanDeck group intentionally across monitors can generate WM_DPICHANGED.
        // That is a window migration, not a display-topology mutation, so do not start the
        // dock/undock stabilizer while a transactional screen-content swap is applying.
        if (_monitorContentSwapInProgress && message == NativeMethods.WM_DPICHANGED) return;
        _displayTransition?.HandleWindowMessage(message);
    }

    private void ScheduleDisplayTransitionCheck(int delayMs, long generation)
    {
        _displayRestoreTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _displayRestoreTimer.Stop();
        _displayRestoreTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 100, 5000));
        _displayRestoreGeneration = generation;
        _displayRestoreTimer.Tick -= DisplayRestoreTimer_Tick;
        _displayRestoreTimer.Tick += DisplayRestoreTimer_Tick;
        _displayRestoreTimer.Start();
    }

    private void DisplayRestoreTimer_Tick(object? sender, EventArgs e)
    {
        _displayRestoreTimer?.Stop();
        _displayTransition?.OnScheduledSettleCheck(_displayRestoreGeneration);
    }

    private void CancelTransientDisplayInteractions()
    {
        _pageGesture?.CancelTracking();
        _looseDesktopHost?.CancelTouchFlick();
        ClearTouchTopologyGesture();
        _touchPagePreviewDirection = null;
        _touchPagePreviewProgress = 0;
        CancelTouchPreviewImmediately();
        foreach (var window in _windows.ToArray())
        {
            try { window.CancelTransientInteractionForDisplayTransition(); } catch { }
        }
    }

    private void RestoreStableDisplayLayout(DisplayTopologySnapshot snapshot, DisplayTransitionRestoreReason reason)
    {
        _displayTransitionStableRestoreCount++;
        if (!_peek)
        {
            try { _host.PrepareShell(); } catch { }
            foreach (var window in _windows.ToArray())
            {
                try { _host.EnsureAttached(window); } catch { }
            }
        }

        var signature = snapshot.Fingerprint;
        var previousSignature = _activeMonitorSignature;
        var previousProfile = _state.MonitorProfiles.FirstOrDefault(p => string.Equals(p.Signature, previousSignature, StringComparison.Ordinal));
        var currentMonitors = MonitorConfigurationService.CaptureMonitors();
        var signatureChanged = !string.Equals(signature, previousSignature, StringComparison.Ordinal);
        var monitorsReduced = MonitorDisconnectLayoutPlanner.IsMonitorReduction(
            _lastStableMonitorCount, currentMonitors.Count);

        if (signatureChanged)
        {
            _activeMonitorSignature = signature;
            _restoringMonitorLayout = true;
            try
            {
                var targetProfile = _state.MonitorProfiles.FirstOrDefault(p =>
                    string.Equals(p.Signature, signature, StringComparison.Ordinal));
                // On monitor loss, restore the saved one-monitor layout if any;
                // otherwise preserve groups already on the surviving monitor
                // and relocate ONLY groups that lost their screen.
                var handledReduction = monitorsReduced &&
                    _state.Settings.LayoutAdjustmentMode != DeckLayoutAdjustmentMode.None &&
                    TryRehomeGroupsAfterMonitorReduction(previousProfile,
                        _state.Settings.LayoutAdjustmentMode == DeckLayoutAdjustmentMode.PerMonitor
                            ? targetProfile : null,
                        currentMonitors);
                if (!handledReduction) switch (_state.Settings.LayoutAdjustmentMode)
                {
                    case DeckLayoutAdjustmentMode.PerMonitor:
                    {
                        if (targetProfile is not null) ApplyPlacements(targetProfile.Fences);
                        else foreach (var window in _windows.ToArray()) window.EnsureVisibleOnMonitor();
                        break;
                    }
                    case DeckLayoutAdjustmentMode.Scale:
                    {
                        if (previousProfile is not null && previousProfile.Fences.Count > 0 && previousProfile.Monitors.Count > 0)
                            ApplyPlacements(ScalePlacements(previousProfile.Fences, previousProfile.Monitors, currentMonitors));
                        else foreach (var window in _windows.ToArray()) window.EnsureVisibleOnMonitor();
                        break;
                    }
                    default:
                        break;
                }
            }
            finally { _restoringMonitorLayout = false; }
        }
        else if (!_peek)
        {
            foreach (var window in _windows.ToArray()) window.EnsureVisibleOnMonitor();
        }

        _lastStableMonitorCount = currentMonitors.Count;
        foreach (var window in _windows.ToArray())
        {
            try { window.RefreshAfterStableDisplayTransition(); } catch (Exception ex) { DiagnosticsLog.Error("Stable display refresh failed", ex); }
        }

        if (!_peek)
        {
            RefreshDesktopTouchPageHost();
            RefreshDesktopFolderPortalDropHost();
            RefreshLooseDesktopIconHost();
        }
        else
        {
            RebuildDesktopMonitorTopology();
        }
        UpdateWindowVisibility();
        // Explorer can re-show native icons while rebuilding FolderView as a
        // monitor disconnects. Restore the intended hidden state only AFTER the
        // monitor topology is stable, then re-check late shell reconstruction.
        StartNativeIconDisplayRepairChecks();
        _calendar?.EnsureBoundsAfterDisplayTransition();
        RefreshPeekCommandSurfaces();
        Persist();
        if (_quickHideStartupStatus == QuickHideStartupApplyStatus.DeferredDisplayTransition)
            _ = TryApplyStartupQuickHideNormalizationAsync();
        DiagnosticsLog.Info($"Display transition stabilized: reason={reason}; monitors={snapshot.MonitorCount}; remote={snapshot.IsRemoteSession}; signature-changed={signatureChanged}; stable-restores={_displayTransitionStableRestoreCount}; deferred-saves={_displayTransitionDeferredSaveCount}");
    }

    private void FlushDeferredDisplayLayoutSave()
    {
        if (!_persistDirty) return;
        PersistNow();
    }

    private MonitorContentSwapCoordinator GetMonitorContentSwapCoordinator() =>
        _monitorContentSwap ??= new MonitorContentSwapCoordinator(
            new MonitorContentSwapPlanner(),
            isDisplayStable: CanRunMonitorContentSwap,
            captureMonitors: CaptureCurrentMonitorSwapDescriptors,
            capturePlacements: CaptureCurrentTopLevelSwapPlacements,
            createUndoCheckpoint: CreateMonitorContentSwapCheckpoint,
            applyMoves: ApplyMonitorContentSwapMoves,
            refreshWindows: RefreshAfterMonitorContentSwap,
            saveState: SaveMonitorContentSwapState,
            diagnostic: message => DiagnosticsLog.Info(message));

    private bool CanRunMonitorContentSwap() =>
        !_safeMode &&
        !_compatibilityReadOnly &&
        !_peek &&
        !_desktopDisplay.IsActive &&
        IsManagedCapabilityAllowed("Layout.SwapScreenContents") &&
        (_displayTransition?.IsStableForUserLayoutAction ?? true);

    private IReadOnlyList<MonitorSwapDescriptor> CaptureCurrentMonitorSwapDescriptors()
    {
        var monitors = MonitorConfigurationService.CaptureMonitors();
        var descriptors = new List<MonitorSwapDescriptor>(monitors.Count);
        foreach (var monitor in monitors)
        {
            var dpi = ResolveBestKnownMonitorDpi(monitor.DeviceName);
            descriptors.Add(new MonitorSwapDescriptor(
                monitor.DeviceName,
                monitor.DeviceName,
                new PhysicalRect(monitor.X, monitor.Y, monitor.Width, monitor.Height),
                new PhysicalRect(monitor.WorkingX, monitor.WorkingY, monitor.WorkingWidth, monitor.WorkingHeight),
                dpi,
                dpi,
                monitor.Primary));
        }
        return descriptors;
    }

    private double ResolveBestKnownMonitorDpi(string monitorId)
    {
        foreach (var window in _windows)
        {
            if (!window.IsLoaded) continue;
            if (!WpfDesktopMonitorSurfaceReader.TryRead(window, $"swap-dpi:{window.Model.Id:D}", out var metric)) continue;
            if (!string.Equals(metric.MonitorKey, monitorId, StringComparison.OrdinalIgnoreCase)) continue;
            if (metric.WindowDpi is >= 48 and <= 960) return metric.WindowDpi;
        }

        var topologyMetric = _desktopMonitorTopology.Surfaces
            .Where(metric => string.Equals(metric.MonitorKey, monitorId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(metric => metric.SurfaceId.StartsWith("monitor:", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

        return topologyMetric.WindowDpi is >= 48 and <= 960 ? topologyMetric.WindowDpi : 96d;
    }

    private IReadOnlyList<ScreenContentPlacement> CaptureCurrentTopLevelSwapPlacements()
    {
        var placements = new List<ScreenContentPlacement>();

        foreach (var window in _windows)
        {
            if (window.TryCaptureMonitorSwapPlacement(out var placement))
                placements.Add(placement);
        }

        foreach (var item in _state.LooseDesktopItems)
        {
            if (item.Id == Guid.Empty ||
                item.PositionX is not double x ||
                item.PositionY is not double y ||
                !double.IsFinite(x) ||
                !double.IsFinite(y))
            {
                continue;
            }

            System.Drawing.Rectangle bounds;
            if (_looseDesktopHost?.TryGetPhysicalBounds(item.Id, out bounds) != true)
            {
                bounds = new System.Drawing.Rectangle(
                    (int)Math.Round(x),
                    (int)Math.Round(y),
                    84,
                    100);
            }

            var center = new System.Drawing.Point(
                bounds.Left + Math.Max(1, bounds.Width) / 2,
                bounds.Top + Math.Max(1, bounds.Height) / 2);
            var screen = Forms.Screen.FromPoint(center);
            placements.Add(new ScreenContentPlacement(
                item.Id.ToString("D"),
                screen.DeviceName,
                ScreenContentKind.LooseIcon,
                new PhysicalRect(bounds.Left, bounds.Top, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height))));
        }

        return placements;
    }

    private bool CreateMonitorContentSwapCheckpoint(string label)
    {
        var path = _store.SaveRecoveryCheckpoint(_state, string.IsNullOrWhiteSpace(label) ? "before-screen-content-swap" : label);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private void ApplyMonitorContentSwapMoves(IReadOnlyList<ScreenContentSwapMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);

        var windowsById = _windows.ToDictionary(window => window.Model.Id);
        var looseById = _state.LooseDesktopItems
            .Where(item => item.Id != Guid.Empty)
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());

        // Validate the entire batch before mutating anything. This keeps an invalid
        // placement from creating a partial transaction before the coordinator can roll back.
        foreach (var move in moves)
        {
            if (!Guid.TryParse(move.ItemId, out var id))
                throw new InvalidOperationException($"Invalid swap item ID: {move.ItemId}");

            switch (move.Kind)
            {
                case ScreenContentKind.Group when !windowsById.ContainsKey(id):
                    throw new InvalidOperationException($"The target group is no longer available: {move.ItemId}");
                case ScreenContentKind.LooseIcon when !looseById.ContainsKey(id):
                    throw new InvalidOperationException($"The target loose icon is no longer available: {move.ItemId}");
                case ScreenContentKind.Other:
                    throw new InvalidOperationException($"Unsupported top-level swap item: {move.ItemId}");
            }
        }

        foreach (var move in moves)
        {
            var id = Guid.Parse(move.ItemId);
            if (move.Kind == ScreenContentKind.Group)
            {
                windowsById[id].ApplyMonitorContentSwapMove(move);
                continue;
            }

            var item = looseById[id];
            item.PositionX = move.AfterPx.X;
            item.PositionY = move.AfterPx.Y;
            _looseDesktopHost?.ApplyMonitorContentSwapMove(id, move.AfterPx);
        }
    }

    private void RefreshAfterMonitorContentSwap()
    {
        foreach (var window in _windows.ToArray())
        {
            try
            {
                _host.EnsureAttached(window);
                window.EnsureVisibleOnMonitor();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Screen-content swap refresh failed for a group", ex);
            }
        }

        RefreshLooseDesktopIconHost();
        RebuildDesktopMonitorTopology();
        UpdateWindowVisibility();
    }

    private void SaveMonitorContentSwapState()
    {
        _persistDirty = true;
        PersistNow(force: true, requireDurableSave: true);
        RebuildTrayMenu();
    }

    private void OpenMonitorContentSwapDialog()
    {
        if (!EnsureManagedCapability("Layout.SwapScreenContents", "화면 내용 교환")) return;
        if (_safeMode)
        {
            System.Windows.MessageBox.Show("안전 모드에서는 화면 내용 교환을 사용할 수 없습니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_compatibilityReadOnly)
        {
            System.Windows.MessageBox.Show("호환성 보호 모드에서는 레이아웃을 변경할 수 없습니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!CanRunMonitorContentSwap())
        {
            System.Windows.MessageBox.Show(
                "현재 디스플레이 구성이 변경 중이거나 Peek/Desktop Display가 활성화되어 있습니다. 화면이 안정된 뒤 다시 시도하세요.",
                "CyanDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IReadOnlyList<MonitorSwapDescriptor> monitors;
        IReadOnlyList<ScreenContentPlacement> placements;
        try
        {
            monitors = CaptureCurrentMonitorSwapDescriptors();
            placements = CaptureCurrentTopLevelSwapPlacements();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to capture screen-content swap preview", ex);
            System.Windows.MessageBox.Show("현재 모니터 배치를 읽지 못했습니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (monitors.Count < 2)
        {
            System.Windows.MessageBox.Show("화면 내용 교환에는 연결된 모니터가 두 대 이상 필요합니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new MonitorContentSwapWindow(monitors);
        var owner = System.Windows.Application.Current?.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(window => window.IsActive && !ReferenceEquals(window, dialog));
        if (owner is not null) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        MonitorContentSwapPlan preview;
        try
        {
            preview = new MonitorContentSwapPlanner().CreatePlan(
                monitors,
                placements,
                dialog.FirstMonitorId,
                dialog.SecondMonitorId,
                dialog.Options);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Screen-content swap preview plan failed", ex);
            System.Windows.MessageBox.Show(ex.Message, "화면 내용 교환", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!preview.HasChanges)
        {
            System.Windows.MessageBox.Show("선택한 두 화면에 교환할 CyanDeck 그룹 또는 그룹 밖 아이콘이 없습니다.", "화면 내용 교환", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var first = monitors.First(m => string.Equals(m.MonitorId, dialog.FirstMonitorId, StringComparison.OrdinalIgnoreCase));
        var second = monitors.First(m => string.Equals(m.MonitorId, dialog.SecondMonitorId, StringComparison.OrdinalIgnoreCase));
        var confirmation =
            $"{first.DisplayName} ↔ {second.DisplayName} 화면 내용을 교환하시겠습니까?\n\n" +
            $"그룹: {preview.GroupMoveCount}개\n" +
            $"그룹 밖 아이콘: {preview.LooseIconMoveCount}개\n\n" +
            "파일 자체는 이동하지 않습니다. 적용 전에 복구 체크포인트가 생성됩니다.";

        if (System.Windows.MessageBox.Show(
                confirmation,
                "화면 내용 교환",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        MonitorContentSwapResult result;
        _monitorContentSwapInProgress = true;
        try
        {
            result = GetMonitorContentSwapCoordinator().Swap(
                dialog.FirstMonitorId,
                dialog.SecondMonitorId,
                dialog.Options);
        }
        finally
        {
            _monitorContentSwapInProgress = false;
        }

        var (message, icon) = result.Status switch
        {
            MonitorContentSwapStatus.Success =>
                ($"화면 내용을 교환했습니다.\n\n그룹 {result.Plan?.GroupMoveCount ?? 0}개 · 그룹 밖 아이콘 {result.Plan?.LooseIconMoveCount ?? 0}개", MessageBoxImage.Information),
            MonitorContentSwapStatus.NoChanges =>
                ("교환할 CyanDeck 내용이 없습니다.", MessageBoxImage.Information),
            MonitorContentSwapStatus.DisplayUnstable =>
                ("디스플레이 구성이 변경 중입니다. 화면이 안정된 뒤 다시 시도하세요.", MessageBoxImage.Information),
            MonitorContentSwapStatus.CheckpointFailed =>
                ("복구 체크포인트를 만들지 못해 화면 내용을 변경하지 않았습니다.", MessageBoxImage.Warning),
            MonitorContentSwapStatus.ApplyFailed =>
                ("화면 내용 교환에 실패했지만 원래 위치로 자동 복원했습니다.", MessageBoxImage.Warning),
            MonitorContentSwapStatus.RollbackFailed =>
                ("화면 내용 교환과 자동 복원이 모두 실패했습니다. 복구 체크포인트를 사용해 이전 배치를 복원하세요.", MessageBoxImage.Error),
            MonitorContentSwapStatus.CaptureFailed =>
                ("현재 모니터/레이아웃 상태를 다시 확인하지 못해 화면 내용을 변경하지 않았습니다.", MessageBoxImage.Warning),
            _ =>
                ("화면 내용 교환 요청을 처리하지 못했습니다.", MessageBoxImage.Warning),
        };

        System.Windows.MessageBox.Show(message, "화면 내용 교환", MessageBoxButton.OK, icon);
    }

    private DeckLayoutSnapshot BuildLayoutSnapshot(string name, bool automatic) => new()
    {
        Name = name,
        CreatedUtc = DateTime.UtcNow,
        IsAutomatic = automatic,
        MonitorSignature = MonitorConfigurationService.GetSignature(),
        CurrentPage = _state.CurrentPage,
        Monitors = MonitorConfigurationService.CaptureMonitors(),
        Fences = CapturePlacements(),
        Items = CaptureItemPlacements()
    };

    private void SaveLayoutSnapshot()
    {
        var snapshot = BuildLayoutSnapshot(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), automatic: false);
        _state.Snapshots.Insert(0, snapshot);
        PruneSnapshots();
        Persist();
        RebuildTrayMenu();
        _ = CaptureSnapshotScreenPreviewAsync(snapshot, SnapshotScreenPreviewCaptureReason.ManualSnapshot);
    }

    private void SaveUndoLayoutSnapshot(string reason)
    {
        _state.Snapshots.Insert(0, BuildLayoutSnapshot($"{reason} {DateTime.Now:yyyy-MM-dd HH:mm:ss}", automatic: false));
        PruneSnapshots();
    }

    private static int CountLayoutDifferences(DeckLayoutSnapshot target, DeckLayoutSnapshot current)
    {
        var currentById = current.Fences.ToDictionary(x => x.FenceId);
        var differences = 0;
        foreach (var p in target.Fences)
        {
            if (!currentById.TryGetValue(p.FenceId, out var c)) { differences++; continue; }
            if (Math.Abs(p.X - c.X) > 1 || Math.Abs(p.Y - c.Y) > 1 || Math.Abs(p.Width - c.Width) > 1 || Math.Abs(p.Height - c.Height) > 1 ||
                p.PageIndex != c.PageIndex || p.RolledUp != c.RolledUp || p.SelectedTabIndex != c.SelectedTabIndex) differences++;
        }
        differences += current.Fences.Count(c => target.Fences.All(t => t.FenceId != c.FenceId));
        return differences;
    }

    private void PerformAutomaticLayout()
    {
        if (!EnsureManagedCapability("Layout.MoveResizeGroups", "자동 레이아웃")) return;
        if (System.Windows.MessageBox.Show(
                "바탕화면을 기본 앱 / 작업 / 미디어 구성으로 다시 정리하시겠습니까?\n\n먼저 전체 복구 체크포인트를 저장합니다. 현재 레이아웃의 그룹별 사용자 지정과 Folder Portal은 기본 구성으로 교체됩니다.",
                "CyanDeck 자동 레이아웃", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            var checkpoint = _store.SaveRecoveryCheckpoint(_state, "before-automatic-layout");
            DiagnosticsLog.Info($"Saved automatic-layout recovery checkpoint {checkpoint}");
            foreach (var window in _windows.ToArray()) window.CloseFromController();
            _windows.Clear();
            _state.Fences.Clear();
            _state.CurrentPage = 0;
            CreateDefaultLayout();
            foreach (var fence in _state.Fences) CreateWindow(fence);
            ReorganizeDesktop();
            UpdateWindowVisibility();
            PersistNow(force: true, requireDurableSave: true);
            _store.RefreshSessionBaseline(_state);
            RebuildTrayMenu();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Automatic layout failed", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 자동 레이아웃 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Called by the per-user Explorer desktop menu through the existing single-
    /// instance pipe (and by the tray fallback).  Does not activate or foreground
    /// any window.  This is a recovery action, so locked groups are included;
    /// their Locked flags and desktop-page membership remain unchanged.
    /// </summary>
    public void RecoverAllGroupsToMonitor(string monitorName)
    {
        if (_disposed || _safeMode || _compatibilityReadOnly ||
            !IsManagedCapabilityAllowed("Layout.MoveResizeGroups")) return;
        if (_displayTransition?.IsStableForUserLayoutAction == false ||
            _monitorContentSwapInProgress) return;
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s =>
            string.Equals(s.DeviceName, monitorName, StringComparison.OrdinalIgnoreCase));
        if (screen is null || _state.Fences.Count == 0) return;
        if (!_desktopRuntimeStarted || _windows.Count != _state.Fences.Count) return;

        List<DeckFencePlacement>? previous = null;
        try
        {
            // A durable checkpoint is created before changing any on-screen group.
            // Failure must leave the existing layout untouched.
            var checkpoint = _store.SaveRecoveryCheckpoint(_state,
                "before-move-all-groups-to-monitor-" + Guid.NewGuid().ToString("N"));
            if (string.IsNullOrWhiteSpace(checkpoint) || !File.Exists(checkpoint))
            {
                DiagnosticsLog.Warn("Group monitor recovery cancelled: checkpoint not saved.");
                _trayController?.ShowBalloonTip(5000, "CyanDeck 그룹 이동 취소",
                    "복구용 백업을 저장하지 못해 기존 그룹 배치를 유지했습니다.", Forms.ToolTipIcon.Warning);
                return;
            }

            var dpi = ResolveBestKnownMonitorDpi(screen.DeviceName);
            var scale = dpi is >= 48 and <= 960 ? dpi / 96d : 1d;
            var work = screen.WorkingArea;
            var rectangle = new MonitorGroupRecoveryPlanner.WorkArea(
                work.Left / scale, work.Top / scale,
                work.Width / scale, work.Height / scale);
            var groups = _state.Fences.Select(f => new MonitorGroupRecoveryPlanner.Group(
                f.Id, f.PageIndex, f.ShowOnAllPages,
                f.Width, f.RolledUp ? Math.Max(140d, f.ExpandedHeight) : f.Height)).ToArray();
            var plan = MonitorGroupRecoveryPlanner.Plan(rectangle, groups, _state.Settings.GroupSnapGap);
            var windows = _windows.ToDictionary(window => window.Model.Id);
            previous = CapturePlacements();
            SaveUndoLayoutSnapshot("Before recover all groups to monitor");
            foreach (var placement in plan.Placements)
            {
                var window = windows[placement.Id];
                var model = window.Model;
                model.X = placement.X;
                model.Y = placement.Y;
                model.Width = placement.Width;
                model.Height = placement.Height;
                model.ExpandedHeight = placement.Height;
                // RolledUp, Locked, ShowOnAllPages and PageIndex are intentionally preserved.
                window.ApplyModelLayout();
            }
            RecoverDesktopVisibility();
            Persist();
            RebuildTrayMenu();
            _trayController?.ShowBalloonTip(4500, "CyanDeck 그룹 위치 복구",
                plan.OverlapUnavoidable
                    ? "이 모니터로 모든 그룹을 옮겼습니다. 그룹 수가 많아 일부 창이 겹칠 수 있습니다."
                    : "모든 그룹을 이 모니터로 배치했습니다. 이전 배치는 레이아웃 스냅샷에서 복원할 수 있습니다.",
                plan.OverlapUnavoidable ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
            DiagnosticsLog.Info($"Recovered {plan.Placements.Count} groups on monitor {screen.DeviceName}; " +
                $"overlap-unavoidable={plan.OverlapUnavoidable}; checkpoint={Path.GetFileName(checkpoint)}");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Move all groups to monitor failed", ex);
            if (previous is not null)
            {
                try { ApplyPlacements(previous); Persist(); }
                catch (Exception rollbackEx) { DiagnosticsLog.Error("Could not roll back monitor group recovery", rollbackEx); }
            }
            ProblemReportService.ShowOnce("recover-groups", "모든 그룹을 현재 모니터로 옮기기", "layout.json", ex,
                "그룹 재배치에 실패했습니다. 기존 레이아웃 복구 지점을 확인하세요.");
        }
    }

    private List<FenceWindow> ArrangeCandidates() => _windows
        .Where(w => !w.Model.Locked && (w.Model.ShowOnAllPages || w.Model.PageIndex == _state.CurrentPage))
        .OrderBy(w => w.Top).ThenBy(w => w.Left).ToList();

    private List<List<FenceWindow>> ArrangeCandidatesByMonitor()
    {
        var monitors = MonitorConfigurationService.CaptureMonitors();
        if (monitors.Count == 0) return [ArrangeCandidates()];
        return ArrangeCandidates().GroupBy(w =>
        {
            var cx = w.Left + w.ActualWidth / 2;
            var cy = w.Top + w.ActualHeight / 2;
            return monitors.FirstOrDefault(m => cx >= m.WorkingX && cx <= m.WorkingX + m.WorkingWidth && cy >= m.WorkingY && cy <= m.WorkingY + m.WorkingHeight)
                ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        }).Select(g => g.ToList()).ToList();
    }

    private void AlignCurrentPageLeft()
    {
        if (!EnsureManagedCapability("Layout.MoveResizeGroups", "그룹 왼쪽 맞춤")) return;
        var groups = ArrangeCandidatesByMonitor().Where(g => g.Count >= 2).ToList();
        if (groups.Count == 0) return;
        SaveUndoLayoutSnapshot("Before align-left");
        foreach (var windows in groups)
        {
            var x = windows.Min(w => w.Left);
            foreach (var w in windows) { w.Left = x; w.Model.X = x; w.EnsureVisibleOnMonitor(); }
        }
        Persist();
    }

    private void AlignCurrentPageTop()
    {
        if (!EnsureManagedCapability("Layout.MoveResizeGroups", "그룹 위쪽 맞춤")) return;
        var groups = ArrangeCandidatesByMonitor().Where(g => g.Count >= 2).ToList();
        if (groups.Count == 0) return;
        SaveUndoLayoutSnapshot("Before align-top");
        foreach (var windows in groups)
        {
            var y = windows.Min(w => w.Top);
            foreach (var w in windows) { w.Top = y; w.Model.Y = y; w.EnsureVisibleOnMonitor(); }
        }
        Persist();
    }

    private void DistributeCurrentPageHorizontally()
    {
        if (!EnsureManagedCapability("Layout.MoveResizeGroups", "가로 간격 균등 배치")) return;
        var groups = ArrangeCandidatesByMonitor().Select(g => g.OrderBy(w => w.Left).ToList()).Where(g => g.Count >= 3).ToList();
        if (groups.Count == 0) return;
        SaveUndoLayoutSnapshot("Before horizontal-distribute");
        foreach (var windows in groups)
        {
            var left = windows.First().Left;
            var right = windows.Last().Left + windows.Last().ActualWidth;
            var totalWidth = windows.Sum(w => Math.Max(1, w.ActualWidth));
            var gap = Math.Max(_state.Settings.GroupSnapGap, (right - left - totalWidth) / (windows.Count - 1));
            var x = left;
            foreach (var w in windows) { w.Left = x; w.Model.X = x; x += Math.Max(1, w.ActualWidth) + gap; }
            foreach (var w in windows) w.EnsureVisibleOnMonitor();
        }
        Persist();
    }

    private void DistributeCurrentPageVertically()
    {
        if (!EnsureManagedCapability("Layout.MoveResizeGroups", "세로 간격 균등 배치")) return;
        var groups = ArrangeCandidatesByMonitor().Select(g => g.OrderBy(w => w.Top).ToList()).Where(g => g.Count >= 3).ToList();
        if (groups.Count == 0) return;
        SaveUndoLayoutSnapshot("Before vertical-distribute");
        foreach (var windows in groups)
        {
            var top = windows.First().Top;
            var bottom = windows.Last().Top + windows.Last().ActualHeight;
            var totalHeight = windows.Sum(w => Math.Max(1, w.ActualHeight));
            var gap = Math.Max(_state.Settings.GroupSnapGap, (bottom - top - totalHeight) / (windows.Count - 1));
            var y = top;
            foreach (var w in windows) { w.Top = y; w.Model.Y = y; y += Math.Max(1, w.ActualHeight) + gap; }
            foreach (var w in windows) w.EnsureVisibleOnMonitor();
        }
        Persist();
    }

    private void PackCurrentPage()
    {
        if (!EnsureManagedCapability("Layout.MoveResizeGroups", "각 모니터 안에 그룹 정리")) return;
        var windows = ArrangeCandidates();
        if (windows.Count < 2) return;
        SaveUndoLayoutSnapshot("Before pack-groups");
        var monitors = MonitorConfigurationService.CaptureMonitors();
        if (monitors.Count == 0) return;
        var gap = Math.Max(4, _state.Settings.GroupSnapGap);
        var grouped = windows.GroupBy(w =>
        {
            var cx = w.Left + w.ActualWidth / 2;
            var cy = w.Top + w.ActualHeight / 2;
            return monitors.FirstOrDefault(m => cx >= m.WorkingX && cx <= m.WorkingX + m.WorkingWidth && cy >= m.WorkingY && cy <= m.WorkingY + m.WorkingHeight)
                ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        });

        foreach (var group in grouped)
        {
            var m = group.Key;
            double x = m.WorkingX + gap;
            double y = m.WorkingY + gap;
            var rowHeight = 0d;
            foreach (var w in group.OrderBy(w => w.Top).ThenBy(w => w.Left))
            {
                var ww = Math.Max(1, w.ActualWidth);
                var wh = Math.Max(1, w.ActualHeight);
                if (x > m.WorkingX + gap && x + ww > m.WorkingX + m.WorkingWidth - gap)
                {
                    x = m.WorkingX + gap;
                    y += rowHeight + gap;
                    rowHeight = 0;
                }
                if (y + wh > m.WorkingY + m.WorkingHeight - gap) y = m.WorkingY + gap;
                w.Left = x; w.Top = y; w.Model.X = x; w.Model.Y = y;
                x += ww + gap;
                rowHeight = Math.Max(rowHeight, wh);
            }
        }
        foreach (var w in windows) w.EnsureVisibleOnMonitor();
        Persist();
    }

    private bool EnsureAutomaticDailySnapshot()
    {
        if (!_state.Settings.EnableAutomaticDailySnapshots) return false;
        var today = DateTime.Today;
        if (_state.Snapshots.Any(s => s.IsAutomatic && s.CreatedUtc.ToLocalTime().Date == today))
        {
            PruneSnapshots();
            return false;
        }

        var snapshot = BuildLayoutSnapshot($"Automatic {today:yyyy-MM-dd}", automatic: true);
        _state.Snapshots.Insert(0, snapshot);
        PruneSnapshots();
        DiagnosticsLog.Info($"Created automatic layout backup for {today:yyyy-MM-dd}");
        _ = CaptureSnapshotScreenPreviewAsync(snapshot, SnapshotScreenPreviewCaptureReason.AutomaticDailyBackup);
        return true;
    }

    private void PruneSnapshots()
    {
        var manualLimit = Math.Clamp(_state.Settings.MaxLayoutSnapshots, 1, 100);
        var automaticLimit = Math.Clamp(_state.Settings.AutomaticSnapshotRetentionDays, 1, 30);
        var keep = new HashSet<Guid>();
        foreach (var snapshot in _state.Snapshots.Where(s => !s.IsAutomatic).OrderByDescending(s => s.CreatedUtc).Take(manualLimit)) keep.Add(snapshot.Id);
        foreach (var snapshot in _state.Snapshots.Where(s => s.IsAutomatic).OrderByDescending(s => s.CreatedUtc).Take(automaticLimit)) keep.Add(snapshot.Id);
        _state.Snapshots.RemoveAll(s => !keep.Contains(s.Id));
        _state.Snapshots = _state.Snapshots.OrderByDescending(s => s.CreatedUtc).ToList();
        try { _snapshotScreenPreviews?.PruneToSnapshotIds(_state.Snapshots.Select(s => s.Id.ToString("D"))); } catch { }
    }

    private void StartAutomaticBackupTimer()
    {
        _dailyBackupTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _dailyBackupTimer.Stop();
        _dailyBackupTimer.Interval = TimeSpan.FromMinutes(30);
        _dailyBackupTimer.Tick -= DailyBackupTimer_Tick;
        _dailyBackupTimer.Tick += DailyBackupTimer_Tick;
        _dailyBackupTimer.Start();
    }

    private void DailyBackupTimer_Tick(object? sender, EventArgs e)
    {
        if (EnsureAutomaticDailySnapshot())
        {
            _persistDirty = true;
            PersistNow();
            RebuildTrayMenu();
        }
        ScheduleAutomaticUpdateCheck();
    }

    private void RestoreLayoutSnapshot(DeckLayoutSnapshot snapshot)
    {
        if (!EnsureManagedCapability("Layout.RestoreSnapshots", "레이아웃 스냅샷 복원")) return;
        var current = BuildLayoutSnapshot("Current layout", automatic: false);
        if (CountLayoutDifferences(snapshot, current) > 0) SaveUndoLayoutSnapshot("Before snapshot restore");
        _state.CurrentPage = Math.Clamp(snapshot.CurrentPage, 0, Math.Max(GetMaxPage(), snapshot.Fences.Count == 0 ? 0 : snapshot.Fences.Max(p => p.PageIndex)));
        ApplyPlacements(snapshot.Fences);
        ApplyItemPlacements(snapshot.Items);
        foreach (var window in _windows) window.RefreshItems();
        UpdateWindowVisibility();
        Persist();
    }

    private List<DeckFencePlacement> CapturePlacements() => _state.Fences.Select(f => new DeckFencePlacement
    {
        FenceId = f.Id, X = f.X, Y = f.Y, Width = f.Width, Height = f.Height,
        ExpandedHeight = f.ExpandedHeight, RolledUp = f.RolledUp, RollupDockEdge = f.RollupDockEdge, Locked = f.Locked,
        MoveTitlebarToBottomNearTop = f.MoveTitlebarToBottomNearTop,
        SelectedTabIndex = f.SelectedTabIndex, PageIndex = f.PageIndex
    }).ToList();

    private List<DeckItemPlacement> CaptureItemPlacements() => _state.Fences
        .SelectMany(f => f.Tabs)
        .Where(t => t.Kind == DeckTabKind.Collection)
        .SelectMany(t => t.Items.Select(i => new DeckItemPlacement
        {
            TabId = t.Id, ItemId = i.Id, Path = i.Path, X = i.PositionX, Y = i.PositionY
        }))
        .ToList();

    private void ApplyItemPlacements(IEnumerable<DeckItemPlacement>? placements)
    {
        if (placements is null) return;
        foreach (var placement in placements)
        {
            var tab = _state.Fences.SelectMany(f => f.Tabs).FirstOrDefault(t => t.Id == placement.TabId);
            if (tab is null) continue;
            var item = tab.Items.FirstOrDefault(i => i.Id == placement.ItemId)
                ?? tab.Items.FirstOrDefault(i => string.Equals(i.Path, placement.Path, StringComparison.OrdinalIgnoreCase));
            if (item is null) continue;
            item.PositionX = placement.X;
            item.PositionY = placement.Y;
        }
    }

    private void ApplyPlacements(IEnumerable<DeckFencePlacement> placements)
    {
        foreach (var placement in placements)
        {
            var fence = _state.Fences.FirstOrDefault(f => f.Id == placement.FenceId);
            if (fence is null) continue;
            fence.X = placement.X;
            fence.Y = placement.Y;
            fence.Width = placement.Width;
            fence.Height = placement.Height;
            fence.ExpandedHeight = placement.ExpandedHeight;
            fence.RolledUp = placement.RolledUp;
            fence.RollupDockEdge = placement.RollupDockEdge;
            fence.Locked = placement.Locked;
            fence.MoveTitlebarToBottomNearTop = placement.MoveTitlebarToBottomNearTop;
            fence.PageIndex = Math.Max(0, placement.PageIndex);
            fence.SelectedTabIndex = Math.Clamp(placement.SelectedTabIndex, 0, Math.Max(0, fence.Tabs.Count - 1));
            _windows.FirstOrDefault(w => w.Model.Id == fence.Id)?.ApplyModelLayout();
        }
    }

    /// <summary>
    /// Rehome only off-screen groups after an actual topology reduction. A
    /// complete saved target profile takes precedence; when none exists, the
    /// previous larger topology profile provides accurate pre-unplug geometry.
    /// Never rewrite the old dual-monitor profile. An on-disk checkpoint is
    /// mandatory before any group changes.
    /// </summary>
    private bool TryRehomeGroupsAfterMonitorReduction(
        DeckMonitorLayoutProfile? previousProfile,
        DeckMonitorLayoutProfile? targetProfile,
        IReadOnlyList<DeckMonitorDescriptor> survivingMonitors)
    {
        try
        {
            if (survivingMonitors.Count == 0 || _state.Fences.Count == 0)
                return false;
            // Incomplete one-monitor profiles are common when new groups were
            // added after the last time the second monitor was disconnected.
            // Start with the previous dual-monitor geometry, then override only
            // entries actually present in the saved target profile.
            var previousSaved = previousProfile?.Fences.ToDictionary(p => p.FenceId);
            var targetSaved = targetProfile?.Fences.ToDictionary(p => p.FenceId);
            var current = CapturePlacements().ToDictionary(p => p.FenceId);
            var areas = survivingMonitors.Select(m =>
            {
                var dpi = ResolveBestKnownMonitorDpi(m.DeviceName);
                var scale = dpi is >= 48 and <= 960 ? dpi / 96d : 1d;
                return new MonitorDisconnectLayoutPlanner.SurvivingArea(
                    m.DeviceName, m.WorkingX / scale, m.WorkingY / scale,
                    m.WorkingWidth / scale, m.WorkingHeight / scale, m.Primary);
            }).ToArray();
            var groups = _state.Fences.Select(f =>
            {
                var source = current[f.Id];
                if (previousSaved?.TryGetValue(f.Id, out var fromPrevious) == true)
                    source = fromPrevious;
                if (targetSaved?.TryGetValue(f.Id, out var fromTarget) == true)
                    source = fromTarget;
                return new MonitorDisconnectLayoutPlanner.DisconnectedGroup(
                    f.Id, f.PageIndex, f.ShowOnAllPages,
                    source.X, source.Y, source.Width,
                    f.RolledUp ? Math.Max(140d, source.ExpandedHeight) : source.Height);
            }).ToArray();
            var plan = MonitorDisconnectLayoutPlanner.Plan(
                areas, groups, _state.Settings.GroupSnapGap);
            var changes = plan.Placements.Where(p =>
            {
                var old = current[p.Id];
                var fence = _state.Fences.First(f => f.Id == p.Id);
                var originalHeight = fence.RolledUp ? old.ExpandedHeight : old.Height;
                return Math.Abs(old.X - p.X) > .5 || Math.Abs(old.Y - p.Y) > .5 ||
                    Math.Abs(old.Width - p.Width) > .5 ||
                    Math.Abs(originalHeight - p.Height) > .5;
            }).ToArray();
            if (changes.Length > 0)
            {
                var checkpoint = _store.SaveRecoveryCheckpoint(_state,
                    "before-auto-monitor-reduction-" + Guid.NewGuid().ToString("N"));
                if (string.IsNullOrWhiteSpace(checkpoint) || !File.Exists(checkpoint))
                {
                    DiagnosticsLog.Warn("Auto monitor rehome skipped: durable recovery checkpoint unavailable.");
                    return false;
                }
                var byId = _state.Fences.ToDictionary(f => f.Id);
                foreach (var placement in changes)
                {
                    var group = byId[placement.Id];
                    group.X = placement.X;
                    group.Y = placement.Y;
                    group.Width = placement.Width;
                    group.Height = placement.Height;
                    group.ExpandedHeight = placement.Height;
                    _windows.FirstOrDefault(w => w.Model.Id == placement.Id)?.ApplyModelLayout();
                }
                DiagnosticsLog.Info($"Auto monitor rehome: {changes.Length} placements updated; " +
                    $"offscreen={plan.RelocatedCount}; overlap-unavoidable={plan.OverlapUnavoidable}; " +
                    $"surviving-monitors={survivingMonitors.Count}; " +
                    $"saved-target-profile={targetProfile is not null}; checkpoint={Path.GetFileName(checkpoint)}");
            }
            else DiagnosticsLog.Info($"Auto monitor rehome: all {_state.Fences.Count} groups already in surviving screen bounds.");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Automatic monitor reduction rehome failed; falling back to existing layout recovery", ex);
            return false;
        }
    }

    private void ApplyInitialLayoutAdjustment()
    {
        // Cold launch after unplug: use the last saved multi-monitor profile
        // when this one-monitor configuration has no saved layout yet.
        var monitors = MonitorConfigurationService.CaptureMonitors();
        var activeProfile = _state.MonitorProfiles.FirstOrDefault(p =>
            string.Equals(p.Signature, _activeMonitorSignature, StringComparison.Ordinal));
        if (_state.Settings.LayoutAdjustmentMode != DeckLayoutAdjustmentMode.None &&
            activeProfile is null)
        {
            var oldProfile = _state.MonitorProfiles.Where(p =>
                MonitorDisconnectLayoutPlanner.IsMonitorReduction(
                    p.Monitors.Count, monitors.Count))
                .OrderByDescending(p => p.UpdatedUtc).FirstOrDefault();
            if (oldProfile is not null &&
                TryRehomeGroupsAfterMonitorReduction(oldProfile, null, monitors))
                return;
        }
        switch (_state.Settings.LayoutAdjustmentMode)
        {
            case DeckLayoutAdjustmentMode.PerMonitor:
                ApplyStoredMonitorProfileToState(_activeMonitorSignature);
                break;
            case DeckLayoutAdjustmentMode.Scale:
            {
                var source = _state.MonitorProfiles.OrderByDescending(p => p.UpdatedUtc).FirstOrDefault();
                if (source is not null && source.Fences.Count > 0 && source.Monitors.Count > 0)
                    ApplyPlacements(ScalePlacements(source.Fences, source.Monitors, MonitorConfigurationService.CaptureMonitors()));
                break;
            }
        }
    }

    private static List<DeckFencePlacement> ScalePlacements(
        IEnumerable<DeckFencePlacement> placements,
        IReadOnlyList<DeckMonitorDescriptor> sourceMonitors,
        IReadOnlyList<DeckMonitorDescriptor> targetMonitors)
    {
        if (sourceMonitors.Count == 0 || targetMonitors.Count == 0) return placements.ToList();
        var targetPrimary = targetMonitors.FirstOrDefault(m => m.Primary) ?? targetMonitors[0];

        static bool Contains(DeckMonitorDescriptor m, double x, double y) =>
            x >= m.WorkingX && x <= m.WorkingX + m.WorkingWidth && y >= m.WorkingY && y <= m.WorkingY + m.WorkingHeight;

        var result = new List<DeckFencePlacement>();
        foreach (var p in placements)
        {
            var cx = p.X + p.Width / 2;
            var cy = p.Y + p.Height / 2;
            var source = sourceMonitors.FirstOrDefault(m => Contains(m, cx, cy))
                ?? sourceMonitors.OrderBy(m => Math.Abs((m.WorkingX + m.WorkingWidth / 2d) - cx) + Math.Abs((m.WorkingY + m.WorkingHeight / 2d) - cy)).First();
            var target = targetMonitors.FirstOrDefault(m => string.Equals(m.DeviceName, source.DeviceName, StringComparison.OrdinalIgnoreCase)) ?? targetPrimary;
            var sx = Math.Max(1d, source.WorkingWidth);
            var sy = Math.Max(1d, source.WorkingHeight);
            var tx = Math.Max(1d, target.WorkingWidth);
            var ty = Math.Max(1d, target.WorkingHeight);
            var scaleX = tx / sx;
            var scaleY = ty / sy;
            var width = Math.Max(140, p.Width * scaleX);
            var height = Math.Max(80, p.Height * scaleY);
            var expandedHeight = Math.Max(80, p.ExpandedHeight * scaleY);
            width = Math.Min(width, Math.Max(140, target.WorkingWidth));
            height = Math.Min(height, Math.Max(80, target.WorkingHeight));
            expandedHeight = Math.Min(expandedHeight, Math.Max(80, target.WorkingHeight));
            var rawX = target.WorkingX + (p.X - source.WorkingX) * scaleX;
            var rawY = target.WorkingY + (p.Y - source.WorkingY) * scaleY;
            var maxX = target.WorkingX + Math.Max(0, target.WorkingWidth - width);
            var maxY = target.WorkingY + Math.Max(0, target.WorkingHeight - height);
            result.Add(new DeckFencePlacement
            {
                FenceId = p.FenceId,
                X = Math.Clamp(rawX, target.WorkingX, maxX),
                Y = Math.Clamp(rawY, target.WorkingY, maxY),
                Width = width,
                Height = height,
                ExpandedHeight = expandedHeight,
                RolledUp = p.RolledUp,
                RollupDockEdge = p.RollupDockEdge,
                Locked = p.Locked,
                MoveTitlebarToBottomNearTop = p.MoveTitlebarToBottomNearTop,
                SelectedTabIndex = p.SelectedTabIndex,
                PageIndex = p.PageIndex
            });
        }
        return result;
    }

    private void ApplyStoredMonitorProfileToState(string signature)
    {
        var profile = _state.MonitorProfiles.FirstOrDefault(p => string.Equals(p.Signature, signature, StringComparison.Ordinal));
        if (profile is null) return;
        foreach (var placement in profile.Fences)
        {
            var fence = _state.Fences.FirstOrDefault(f => f.Id == placement.FenceId);
            if (fence is null) continue;
            fence.X = placement.X; fence.Y = placement.Y; fence.Width = placement.Width; fence.Height = placement.Height;
            fence.ExpandedHeight = placement.ExpandedHeight; fence.RolledUp = placement.RolledUp; fence.RollupDockEdge = placement.RollupDockEdge; fence.Locked = placement.Locked;
            fence.MoveTitlebarToBottomNearTop = placement.MoveTitlebarToBottomNearTop;
            fence.SelectedTabIndex = placement.SelectedTabIndex; fence.PageIndex = Math.Max(0, placement.PageIndex);
        }
    }

    private void CaptureMonitorProfile()
    {
        if (_state.Settings.LayoutAdjustmentMode == DeckLayoutAdjustmentMode.None) return;
        if (_restoringMonitorLayout || _geometryResetPendingRestart ||
            string.IsNullOrWhiteSpace(_activeMonitorSignature)) return;
        var current = MonitorConfigurationService.GetSignature();
        if (!string.Equals(current, _activeMonitorSignature, StringComparison.Ordinal)) return;
        var profile = _state.MonitorProfiles.FirstOrDefault(p => string.Equals(p.Signature, _activeMonitorSignature, StringComparison.Ordinal));
        if (profile is null)
        {
            profile = new DeckMonitorLayoutProfile { Signature = _activeMonitorSignature };
            _state.MonitorProfiles.Add(profile);
        }
        profile.UpdatedUtc = DateTime.UtcNow;
        profile.Monitors = MonitorConfigurationService.CaptureMonitors();
        profile.Fences = CapturePlacements();
        if (_state.MonitorProfiles.Count > 12)
            _state.MonitorProfiles = _state.MonitorProfiles.OrderByDescending(p => p.UpdatedUtc).Take(12).ToList();
    }

    private int GetMaxPage() => Math.Max(0, _state.Fences.Count == 0 ? 0 : _state.Fences.Max(f => f.PageIndex));

    private DesktopPageTransitionOptions GetPageTransitionOptions() => new()
    {
        AnimationsEnabled = _state.Settings.AnimatePageTransitions && !_safeMode,
        EnhancedEasingEnabled = _state.Settings.EnhancedDesktopPageEasing,
        EasingProfile = _state.Settings.DesktopPageEasingProfile,
        DurationMilliseconds = _state.Settings.PageTransitionDurationMs,
        LightBounceEnabled = _state.Settings.BouncePageTransitions,
    };

    private void SwitchPage(int direction) => SwitchPage(direction, 0d);

    private void SwitchPage(int direction, double startingProgress)
    {
        if (direction == 0 || _peek) return;
        var target = _state.CurrentPage + Math.Sign(direction);
        if (target < 0 || target > GetMaxPage()) return;
        TransitionToPage(target, Math.Sign(direction), startingProgress);
    }

    private void GoToPage(int page)
    {
        var target = Math.Clamp(page, 0, GetMaxPage());
        if (target == _state.CurrentPage) return;
        TransitionToPage(target, target > _state.CurrentPage ? 1 : -1, 0d);
    }

    private void TransitionToPage(int target, int direction, double startingProgress = 0d)
    {
        if (startingProgress <= 0)
        {
            CancelTouchPagePullPreview();
        }
        else
        {
            _touchPagePreviewDirection = null;
            _touchPagePreviewProgress = 0;
        }

        target = Math.Clamp(target, 0, GetMaxPage());
        if (target == _state.CurrentPage) return;
        if ((_quickHidden || HasQuickHideHiddenVisualResidue()) && _state.Settings.UnhideWhenSwitchingPages)
            EnsureQuickHideVisible(QuickHideShowReason.PageSwitchAutoUnhide, QuickHideInvocationSource.PageSwitchAutoUnhide);
        var oldPage = _state.CurrentPage;
        _state.CurrentPage = target;
        var visibleGroupCount = _state.Fences.Count(f => f.ShowOnAllPages || f.PageIndex == target);
        DiagnosticsLog.Info($"Desktop page changed: {oldPage + 1}->{target + 1}; visible-groups={visibleGroupCount}; quick-hidden={_quickHidden}");
        var token = ++_pageTransitionToken;

        var transitionDirection = direction < 0
            ? DesktopPageTransitionDirection.Previous
            : DesktopPageTransitionDirection.Next;
        var transitionOptions = GetPageTransitionOptions();
        var systemAnimationsAllowed = WpfDesktopPageTransitionAnimationFactory.AreSystemAnimationsAllowed();

        if (!_state.Settings.AnimatePageTransitions || _quickHidden || direction == 0 || !systemAnimationsAllowed ||
            !ArePageTransitionSurfacesDpiSynchronized(oldPage, target))
        {
            UpdateWindowVisibility();
            Persist();
            RebuildTrayMenu();
            return;
        }

        foreach (var window in _windows)
        {
            if (window.Model.ShowOnAllPages)
            {
                if (!_quickHidden || window.Model.AlwaysVisible)
                {
                    EnsureFenceRuntimeVisible(window);
                    window.EnsureItemsRendered();
                }
                continue;
            }

            var request = new DesktopPageTransitionRequest(
                transitionDirection,
                DesktopPageTransitionPhase.Commit,
                window.GetPageTransitionSurfaceWidthDip(),
                startingProgress,
                systemAnimationsAllowed);
            var plan = DesktopPageTransitionPlanner.Create(request, transitionOptions);

            if (window.Model.PageIndex == target)
            {
                var quickVisible = !_quickHidden || !_state.Settings.EnableQuickHide || _state.Settings.QuickHideMode == DeckQuickHideMode.UngroupedIconsOnly || window.Model.AlwaysVisible;
                if (!quickVisible) { window.Hide(); continue; }
                EnsureFenceRuntimeVisible(window);
                window.EnsureItemsRendered();
                window.AnimatePageIn(plan);
            }
            else if (window.Model.PageIndex == oldPage && window.IsVisible)
            {
                window.AnimatePageOut(plan, () =>
                {
                    if (token == _pageTransitionToken && !window.Model.ShowOnAllPages && window.Model.PageIndex != _state.CurrentPage)
                        window.Hide();
                });
            }
            else if (window.IsVisible)
            {
                window.Hide();
            }
        }
        UpdateTrayText();
        Persist();
        RebuildTrayMenu();
    }

    private bool TryMoveFenceAcrossPageEdge(FenceWindow source)
    {
        if (!IsManagedCapabilityAllowed("Layout.MoveResizeGroups")) return false;
        if (!_state.Settings.EnableEdgePageGestures) return false;
        var direction = source.GetPageEdgeDirection();
        if (direction == 0) return false;
        if (direction < 0 && source.Model.PageIndex <= 0) return false;
        var targetPage = Math.Max(0, source.Model.PageIndex + direction);
        source.Model.PageIndex = targetPage;
        source.MoveFromPageEdge(direction);
        if (targetPage != _state.CurrentPage) TransitionToPage(targetPage, direction);
        else
        {
            UpdateWindowVisibility();
            Persist();
            RebuildTrayMenu();
        }
        return true;
    }


    // Explicit user action only; not part of automatic display/Explorer repair
    // or the group-to-monitor recovery pipeline. Prevents an unnecessary
    // calendar Z-order retry when the desktop surface is already healthy.
    private void RecoverVisibleSurfacesFromTray()
    {
        if (_disposed) return;
        var plan = DisplaySurfaceRecoveryPlan.Create(
            _safeMode,
            _state.Settings.EnableCyanDeck,
            _desktopRuntimeStarted,
            _state.Settings.EnableCalendar,
            _displayTransition?.IsStableForUserLayoutAction != false);
        if (plan.BlockedByDisplayTransition)
        {
            _trayController?.ShowBalloonTip(4500, "CyanDeck 표시 복구",
                "모니터 변경 처리가 진행 중입니다. 화면이 안정된 뒤 다시 실행하세요.",
                Forms.ToolTipIcon.Warning);
            return;
        }
        if (!plan.HasWork)
        {
            _trayController?.ShowBalloonTip(4500, "CyanDeck 표시 복구",
                "복구할 그룹 또는 캘린더가 활성화되어 있지 않습니다.",
                Forms.ToolTipIcon.Info);
            return;
        }

        var groupsRepaired = false;
        var calendarResult = "";
        try
        {
            if (plan.RepairGroups)
            {
                RecoverDesktopVisibility();
                groupsRepaired = true;
            }
            if (plan.InspectCalendar)
            {
                StartCalendarModule();
                if (_calendar?.IsSurfaceVisible == true)
                    calendarResult = "캘린더 표시 정상";
                else if (_calendar?.TryRepairVisibleSurface() == true)
                    calendarResult = "캘린더 표시 복구 완료";
                else
                    calendarResult = "캘린더 표시 실패 또는 재시도 제한 — 캘린더 상태 진단 확인";
            }
            var groupResult = groupsRepaired ? "그룹 표시 복구 완료" : "";
            var result = string.Join(" · ", new[] { groupResult, calendarResult }
                .Where(value => !string.IsNullOrEmpty(value)));
            DiagnosticsLog.Info("Tray surface recovery: " + result);
            _trayController?.ShowBalloonTip(5000, "CyanDeck 표시 복구",
                result,
                calendarResult.Contains("실패", StringComparison.Ordinal)
                    ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
            RebuildTrayMenu();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Unified tray visibility recovery failed", ex);
            _trayController?.ShowBalloonTip(5000, "CyanDeck 표시 복구 실패",
                "표시 상태 진단을 확인하세요. 기존 그룹과 일정 데이터는 변경하지 않았습니다.",
                Forms.ToolTipIcon.Warning);
        }
    }

    private void RecoverDesktopVisibility()
    {
        if (_safeMode) return;
        if (_peek) SetPeek(false);
        if (_desktopDisplay.IsActive) _desktopDisplay.SetActive(false);
        EnsureQuickHideVisible(QuickHideShowReason.RecoveryRepair, QuickHideInvocationSource.Programmatic);

        var currentHasGroups = _state.Fences.Any(f => f.ShowOnAllPages || f.PageIndex == _state.CurrentPage);
        if (!currentHasGroups && _state.Fences.Count > 0)
        {
            var targetPage = _state.Fences
                .Where(f => !f.ShowOnAllPages)
                .Select(f => Math.Max(0, f.PageIndex))
                .Distinct()
                .OrderBy(page => Math.Abs(page - _state.CurrentPage))
                .ThenBy(page => page)
                .Cast<int?>()
                .FirstOrDefault();
            if (targetPage is int page && page != _state.CurrentPage)
                GoToPage(page);
        }

        ApplyNativeDesktopVisibility();
        foreach (var window in _windows)
        {
            try { _host.EnsureAttached(window); } catch { }
        }
        UpdateWindowVisibility();
        RepairExpectedFenceVisibility();
        RefreshDesktopTouchPageHost();
        RefreshDesktopFolderPortalDropHost();
        RefreshLooseDesktopIconHost();
        DiagnosticsLog.Info($"Manual visibility recovery completed; page={_state.CurrentPage + 1}; quick-hidden={_quickHidden}; desktop-display={_desktopDisplay.IsActive}");
        RebuildTrayMenu();
    }


    private void OpenSnapshotGallery()
    {
        var gallery = new SnapshotGalleryWindow(_state.Snapshots, BuildLayoutSnapshot("Current layout", automatic: false),
            RestoreLayoutSnapshot,
            snapshot =>
            {
                _state.Snapshots.Remove(snapshot);
                try { _snapshotScreenPreviews?.Delete(snapshot.Id.ToString("D")); } catch { }
                Persist();
                RebuildTrayMenu();
            },
            () =>
            {
                Persist();
                RebuildTrayMenu();
            },
            snapshot => _snapshotScreenPreviews?.TryLoad(snapshot.Id.ToString("D")));
        gallery.Show();
        gallery.Activate();
    }

    private void StartCalendarModule()
    {
        if (!FeatureRuntimePlan.Create(_state.Settings.EnableCyanDeck,
                _state.Settings.EnableCalendar, _safeMode).StartCalendar) return;
        try
        {
            _calendar ??= new CalendarModule(_host, _state.Settings, () => Persist(), ownsShellRecovery: !_desktopRuntimeStarted);
            _calendar.Start();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Calendar startup failed; CyanDeck remains available", ex);
            _calendar?.Dispose();
            _calendar = null;
        }
    }

    private void SetCalendarEnabled(bool enabled)
    {
        if (_compatibilityReadOnly) return;
        var result = FeatureSwitchCheckpoint.Apply(
            () => _state.Settings.EnableCalendar,
            value => _state.Settings.EnableCalendar = value,
            enabled,
            () => PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true),
            ex => ReportFeatureSwitchSaveFailure("캘린더", ex));
        if (result != FeatureSwitchCheckpoint.Outcome.Applied) return;

        // The durable setting always changes before a window is opened or closed.
        if (enabled)
        {
            if (_safeMode)
                _trayController?.ShowBalloonTip(5000, "CyanDeck 캘린더",
                    "안전 모드에서는 캘린더가 표시되지 않습니다. 일반 모드로 다시 시작하세요.",
                    Forms.ToolTipIcon.Warning);
            else
            {
                StartCalendarModule();
                if (_calendar?.IsSurfaceVisible != true)
                    _trayController?.ShowBalloonTip(5000, "CyanDeck 캘린더",
                        "캘린더가 바탕화면에 연결되지 않았습니다. 트레이의 '캘린더 표시 복구'를 실행하세요.",
                        Forms.ToolTipIcon.Warning);
            }
        }
        else { _calendar?.Dispose(); _calendar = null; }
        AuditSettingsChanges("Calendar module toggle");
        RebuildTrayMenu();
    }

    private void SetCyanDeckEnabled(bool enabled)
    {
        if (_compatibilityReadOnly) return;
        var result = FeatureSwitchCheckpoint.Apply(
            () => _state.Settings.EnableCyanDeck,
            value => _state.Settings.EnableCyanDeck = value,
            enabled,
            () => PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true),
            ex => ReportFeatureSwitchSaveFailure("CyanDeck", ex));
        if (result != FeatureSwitchCheckpoint.Outcome.Applied) return;

        AuditSettingsChanges("CyanDeck module toggle");
        // The Deck runtime owns Explorer icon visibility, hooks and watchers.
        // Restart only after the requested setting has reached disk.
        Restart(false);
    }

    private static void ReportFeatureSwitchSaveFailure(string feature, Exception ex)
    {
        DiagnosticsLog.Error($"Cannot save {feature} feature switch", ex);
        ProblemReportService.ShowOnce("layout-save", $"{feature} 기능 설정 저장", "layout.json", ex,
            "사용자가 요청한 기능 설정을 저장하지 못했습니다. 이전 실행 상태를 유지합니다.",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck", "layout.json"));
    }

    private void CreateTray()
    {
        // A persistent menu and icon are owned by a dedicated WinForms adapter.
        // Keep the AppController callbacks on the dispatcher (as before).
        _trayController?.Dispose();
        _trayController = new TrayIconController(
            PopulateTrayMenu,
            () =>
            {
                if (_safeMode || !_state.Settings.EnableCyanDeck) QueueOpenSettingsWindow();
                else ToggleQuickHide(QuickHideInvocationSource.Tray);
            },
            visible => _trayMenuVisible = visible);
        _trayController.NativeMenuClosed += () =>
        {
            if (_pendingTrayDialog is not { } show) return;
            _pendingTrayDialog = null;
            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, show);
        };
        UpdateTrayText();
        // The updater can fail after replacing only some managed files. Offer a
        // durable, user-approved restoration on the next successful app launch.
        if (UpdateService.HasPendingRecovery())
            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OfferPendingUpdateRecovery));
    }

    private void OfferPendingUpdateRecovery()
    {
        if (!UpdateService.HasPendingRecovery()) return;
        var answer = System.Windows.MessageBox.Show(
            "이전 CyanDeck 업데이트가 완료되지 않아 복구 기록이 남아 있습니다.\n" +
            "프로그램을 종료하고 백업한 실행 파일을 복원하시겠습니까?\n" +
            "캘린더 일정과 사용자 파일은 변경하지 않습니다. 복구를 연기하면 새 업데이트가 차단됩니다.",
            "CyanDeck 업데이트 복구", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes) LaunchPendingUpdateRecovery();
    }

    private void LaunchPendingUpdateRecovery()
    {
        try
        {
            // The independent script waits for normal process exit. Never force-kill
            // CyanDeck while settings/calendar writes may still be pending.
            if (UpdateService.TryLaunchPendingRecovery(Environment.ProcessId)) Exit();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Could not start pending update recovery", ex);
            System.Windows.MessageBox.Show("업데이트 복구를 실행하지 못했습니다.\n" + ex.Message,
                "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RebuildTrayMenu()
    {
        // Menu contents are rebuilt on Opening, never during an active popup.
        if (_trayController is not null) UpdateTrayText();
    }

    private void PopulateTrayMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        // Always-visible build identity: old running instances will not show this line.
        menu.Items.Add(new Forms.ToolStripMenuItem(
            $"CyanDeck · v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}")
        { Enabled = false, Tag = "brand.identity" });
        menu.Items.Add(new Forms.ToolStripMenuItem("캘린더 상태: " +
            (_safeMode ? "안전 모드" : !_state.Settings.EnableCalendar ? "설정 꺼짐"
                : _calendar?.GetTrayDisplayStatus() ?? "시작 대기/상태 확인 필요"))
        { Enabled = false, Tag = "calendar.status" });
        menu.Items.Add("캘린더 표시 복구", null, (_, _) =>
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RepairCalendarSurface)));
        menu.Items.Add("캘린더 상태 진단…", null, (_, _) => QueueTrayDialog(ShowCalendarDiagnostics));
        if (!_safeMode && _state.Settings.EnableCalendar)
            menu.Items.Add("캘린더 일반 창으로 열기 (표시 실패 시)", null, (_, _) =>
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OpenCalendarStandaloneWindow)));
        menu.Items.Add("캘린더 위치 초기화 / 표시 복구", null, (_, _) =>
            QueueTrayDialog(ResetCalendarPositionAndRepair));
        // Available in combined and calendar-only modes. The command respects
        // display-transition barriers and the calendar's 15-second retry limit.
        if (!_safeMode && (_state.Settings.EnableCyanDeck || _state.Settings.EnableCalendar))
            menu.Items.Add("표시 상태 복구", null, (_, _) =>
                _dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(RecoverVisibleSurfacesFromTray)));
        menu.Items.Add("Windows Explorer 기본 아이콘 긴급 복구", null, (_, _) =>
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RescueNativeExplorerIcons)));
        if (UpdateService.HasPendingRecovery())
            menu.Items.Add("미완료 업데이트 백업 복구…", null, (_, _) =>
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OfferPendingUpdateRecovery)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (!_safeMode)
        {
            var deckSwitch = new Forms.ToolStripMenuItem("CyanDeck 그룹·아이콘 관리 사용")
            {
                Checked = _state.Settings.EnableCyanDeck,
                Enabled = !_compatibilityReadOnly,
                Tag = "group.enable"
            };
            deckSwitch.Click += (_, _) =>
            {
                var enabled = !deckSwitch.Checked;
                _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => SetCyanDeckEnabled(enabled)));
            };
            menu.Items.Add(deckSwitch);
            var calendarSwitch = new Forms.ToolStripMenuItem("바탕화면 캘린더 사용")
            {
                Checked = _state.Settings.EnableCalendar,
                Enabled = !_compatibilityReadOnly,
                Tag = "calendar.enable"
            };
            calendarSwitch.Click += (_, _) =>
            {
                var enabled = !calendarSwitch.Checked;
                _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => SetCalendarEnabled(enabled)));
            };
            menu.Items.Add(calendarSwitch);
            // Status and repair above remain available even when the switch has not been saved.
            if (_state.Settings.EnableCalendar)
            {
                menu.Items.Add("캘린더 오늘로 이동", null, (_, _) => _dispatcher.BeginInvoke(new Action(() => _calendar?.GoToToday())));
                menu.Items.Add("Google Calendar 동기화", null, (_, _) => _dispatcher.BeginInvoke(new Action(() => _ = _calendar?.SynchronizeAsync())));
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        if (!_safeMode && !_state.Settings.EnableCyanDeck)
        {
            menu.Items.Add("설정…", null, (_, _) => QueueOpenSettingsWindow());
            menu.Items.Add("시작 가이드 다시 보기…", null, (_, _) =>
                QueueTrayDialog(OpenGettingStartedGuide));
            menu.Items.Add("설정 폴더 열기", null, (_, _) => OpenSettingsFolder());
            menu.Items.Add("진단 로그 열기", null, (_, _) => OpenDiagnosticLogs());
            menu.Items.Add("종료", null, (_, _) => _dispatcher.Invoke(Exit));
            CompactTrayMenu(menu);
            UpdateTrayText();
            return;
        }
        if (_safeMode)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem("안전 모드 활성화 — 바탕화면 통합 비활성") { Enabled = false });
            menu.Items.Add("설정…", null, (_, _) => QueueOpenSettingsWindow());
            menu.Items.Add(new Forms.ToolStripMenuItem("시작 가이드 다시 보기… (일반 모드에서 사용)") { Enabled = false });
            menu.Items.Add("설정 검사 / 복구", null, (_, _) => _dispatcher.Invoke(ValidateAndRepairConfiguration));
            menu.Items.Add("설정 폴더 열기", null, (_, _) => OpenSettingsFolder());
            menu.Items.Add("진단 로그 열기", null, (_, _) => OpenDiagnosticLogs());
            menu.Items.Add("진단 묶음 만들기…", null, (_, _) => _dispatcher.Invoke(CreateDiagnosticBundle));
            menu.Items.Add("업데이트 확인…", null, (_, _) => _dispatcher.BeginInvoke(new Action(() => _ = CheckForUpdatesInteractiveAsync())));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("일반 모드로 다시 시작", null, (_, _) => _dispatcher.Invoke(() => Restart(false)));
            menu.Items.Add("종료", null, (_, _) => _dispatcher.Invoke(Exit));
            CompactTrayMenu(menu);
            return;
        }
        var newGroupMenuItem = menu.Items.Add("새 그룹", null, (_, _) => _dispatcher.Invoke(CreateNewFence));
        newGroupMenuItem.Tag = "group.new";
        newGroupMenuItem.Enabled = IsManagedCapabilityAllowed("Layout.CreateDeleteGroups");
        menu.Items.Add("휴지통 위치 초기화", null,
            (_, _) => _dispatcher.Invoke(ResetStandaloneRecycleBinPosition));
        var systemGroupMenuItem = menu.Items.Add("Windows 기본 아이콘 그룹 추가", null,
            (_, _) => _dispatcher.Invoke(AddSystemIconGroup));
        systemGroupMenuItem.Enabled = IsManagedCapabilityAllowed("Layout.CreateDeleteGroups");
        if (_state.Templates.Count > 0)
        {
            var templates = new Forms.ToolStripMenuItem("템플릿에서 새 그룹 만들기")
            {
                Enabled = IsManagedCapabilityAllowed("Layout.CreateDeleteGroups")
            };
            foreach (var template in _state.Templates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var local = template;
                templates.DropDownItems.Add(local.Name, null, (_, _) => _dispatcher.Invoke(() => CreateFenceFromTemplate(local.Id)));
            }
            templates.DropDownItems.Add(new Forms.ToolStripSeparator());
            var deleteTemplatesItem = templates.DropDownItems.Add("모든 템플릿 삭제", null, (_, _) => _dispatcher.Invoke(() =>
            {
                if (!EnsureManagedCapability("Templates.Edit", "모든 템플릿 삭제")) return;
                if (System.Windows.MessageBox.Show("저장된 그룹 템플릿을 모두 삭제하시겠습니까?", "CyanDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _state.Templates.Clear();
                Persist();
                RebuildTrayMenu();
            }));
            deleteTemplatesItem.Enabled = IsManagedCapabilityAllowed("Templates.Edit");
            menu.Items.Add(templates);
        }
        menu.Items.Add("바탕화면 분류 규칙 다시 적용", null, (_, _) => _dispatcher.Invoke(ApplyRulesNow));
        var pages = new Forms.ToolStripMenuItem($"바탕화면 페이지 (페이지 {_state.CurrentPage + 1})");
        for (var page = 0; page <= GetMaxPage(); page++)
        {
            var localPage = page;
            var item = new Forms.ToolStripMenuItem($"페이지 {page + 1}")
            {
                Checked = page == _state.CurrentPage,
                CheckOnClick = false
            };
            item.Click += (_, _) => _dispatcher.Invoke(() => GoToPage(localPage));
            pages.DropDownItems.Add(item);
        }
        pages.Tag = "desktop.pages";
        menu.Items.Add(pages);

        var arrange = new Forms.ToolStripMenuItem("현재 페이지 정렬")
        {
            Enabled = IsManagedCapabilityAllowed("Layout.MoveResizeGroups")
        };
        arrange.DropDownItems.Add("그룹 왼쪽 맞춤", null, (_, _) => _dispatcher.Invoke(AlignCurrentPageLeft));
        arrange.DropDownItems.Add("그룹 위쪽 맞춤", null, (_, _) => _dispatcher.Invoke(AlignCurrentPageTop));
        arrange.DropDownItems.Add("가로 간격 균등 배치", null, (_, _) => _dispatcher.Invoke(DistributeCurrentPageHorizontally));
        arrange.DropDownItems.Add("세로 간격 균등 배치", null, (_, _) => _dispatcher.Invoke(DistributeCurrentPageVertically));
        arrange.DropDownItems.Add("각 모니터 안에 그룹 정리", null, (_, _) => _dispatcher.Invoke(PackCurrentPage));
        menu.Items.Add(arrange);
        var monitorRecovery = menu.Items.Add("이 모니터로 그룹 전부 옮기기", null, (_, _) =>
        {
            var monitorName = Forms.Screen.FromPoint(Forms.Cursor.Position).DeviceName;
            _dispatcher.BeginInvoke(new Action(() => RecoverAllGroupsToMonitor(monitorName)));
        });
        monitorRecovery.Enabled = IsManagedCapabilityAllowed("Layout.MoveResizeGroups") && _state.Fences.Count > 0;
        if (Forms.Screen.AllScreens.Length >= 2)
        {
            var swapItem = menu.Items.Add("화면 내용 교환…", null, (_, _) => _dispatcher.Invoke(OpenMonitorContentSwapDialog));
            swapItem.Enabled = IsManagedCapabilityAllowed("Layout.SwapScreenContents");
        }

        var snapshots = new Forms.ToolStripMenuItem("레이아웃 스냅샷") { Tag = "group.snapshots" };
        snapshots.DropDownItems.Add("현재 레이아웃 저장", null, (_, _) => _dispatcher.Invoke(SaveLayoutSnapshot));
        snapshots.DropDownItems.Add("스냅샷 미리보기 / 관리…", null, (_, _) => _dispatcher.Invoke(OpenSnapshotGallery));
        snapshots.DropDownItems.Add("자동 레이아웃 실행…", null, (_, _) => _dispatcher.Invoke(PerformAutomaticLayout));
        if (_state.Snapshots.Count > 0)
        {
            snapshots.DropDownItems.Add(new Forms.ToolStripSeparator());
            foreach (var snapshot in _state.Snapshots.Take(12))
            {
                var local = snapshot;
                var restoreSnapshotItem = snapshots.DropDownItems.Add(local.IsAutomatic ? $"[자동] {local.Name}" : local.Name, null, (_, _) => _dispatcher.Invoke(() => RestoreLayoutSnapshot(local)));
                restoreSnapshotItem.Enabled = IsManagedCapabilityAllowed("Layout.RestoreSnapshots");
            }
            snapshots.DropDownItems.Add(new Forms.ToolStripSeparator());
            snapshots.DropDownItems.Add("모든 스냅샷 삭제", null, (_, _) => _dispatcher.Invoke(() =>
            {
                _state.Snapshots.Clear();
                Persist();
                RebuildTrayMenu();
            }));
        }
        menu.Items.Add(snapshots);
        var peekCommand = menu.Items.Add(_peek ? "Peek 종료" : "Peek", null,
            (_, _) => _dispatcher.Invoke(TogglePeek));
        peekCommand.Tag = "desktop.peek";
        if (_state.Settings.EnableDesktopDisplay)
        {
            var display = menu.Items.Add(_desktopDisplay.IsActive ? "Desktop Display 종료" : "Desktop Display", null,
                (_, _) => _dispatcher.Invoke(() => { _desktopDisplay.Toggle(); UpdateWindowVisibility(); RefreshDesktopTouchPageHost(); RefreshDesktopFolderPortalDropHost(); RefreshLooseDesktopIconHost(); UpdateTrayText(); RebuildTrayMenu(); }));
            display.Tag = "desktop.display";
        }
        if (_state.Settings.EnableQuickHide)
        {
            var quickLabel = _quickHidden ? "빠른 숨김 종료" : "빠른 숨김";
            var quickItem = menu.Items.Add(quickLabel, null, (_, _) => _dispatcher.Invoke(() => ToggleQuickHide(QuickHideInvocationSource.Tray)));
            quickItem.Enabled = _state.Settings.EnableQuickHide || _quickHidden;
            quickItem.Tag = "desktop.quick-hide";
        }
        var nativeIcons = menu.Items.Add(
            _state.Settings.HideNativeDesktopIcons ? "Windows 바탕화면 아이콘 표시" : "Windows 바탕화면 아이콘 숨기기",
            null, (_, _) => _dispatcher.Invoke(ToggleNativeIcons));
        nativeIcons.Tag = "desktop.native-icons";
        var backup = new Forms.ToolStripMenuItem("백업 / 복원") { Tag = "recovery.backup" };
        backup.DropDownItems.Add("레이아웃 및 설정 내보내기…", null, (_, _) => _dispatcher.Invoke(ExportConfiguration));
        var importMenuItem = backup.DropDownItems.Add("레이아웃 및 설정 가져오기…", null, (_, _) => _dispatcher.Invoke(() => ImportConfiguration()));
        importMenuItem.Enabled = IsManagedCapabilityAllowed("ImportExport.Import");
        var recovery = new Forms.ToolStripMenuItem("복구 체크포인트");
        try
        {
            var recoveryFiles = Directory.Exists(_store.RecoveryFolder)
                ? new DirectoryInfo(_store.RecoveryFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(10).ToArray()
                : Array.Empty<FileInfo>();
            if (recoveryFiles.Length == 0) recovery.Enabled = false;
            foreach (var file in recoveryFiles)
            {
                var path = file.FullName;
                var label = $"{file.LastWriteTime:yyyy-MM-dd HH:mm} · {Path.GetFileNameWithoutExtension(file.Name)}";
                recovery.DropDownItems.Add(label, null, (_, _) => _dispatcher.Invoke(() => RestoreRecoveryCheckpoint(path)));
            }
            if (recoveryFiles.Length > 0)
            {
                recovery.DropDownItems.Add(new Forms.ToolStripSeparator());
                recovery.DropDownItems.Add("복구 폴더 열기", null, (_, _) => Process.Start(new ProcessStartInfo(_store.RecoveryFolder) { UseShellExecute = true }));
            }
        }
        catch { recovery.Enabled = false; }
        backup.DropDownItems.Add(recovery);
        menu.Items.Add(backup);
        if (!string.IsNullOrWhiteSpace(_uncleanRecoveryPath))
        {
            var interrupted = new Forms.ToolStripMenuItem("이전 세션 복구 데이터 사용 가능…");
            interrupted.Click += (_, _) => _dispatcher.Invoke(() => RestoreRecoveryCheckpoint(_uncleanRecoveryPath!));
            menu.Items.Add(interrupted);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        var startup = new Forms.ToolStripMenuItem("Windows 시작 시 실행")
        {
            Checked = _state.Settings.StartWithWindows,
            CheckOnClick = true,
            Tag = "settings.startup",
            Enabled = IsManagedCapabilityAllowed("Settings.EditGeneral")
        };
        startup.CheckedChanged += (_, _) => _dispatcher.Invoke(() =>
        {
            if (!EnsureManagedCapability("Settings.EditGeneral", "Windows 시작 시 실행 설정", showNotice: false)) return;
            _state.Settings.StartWithWindows = startup.Checked;
            AuditSettingsChanges("Tray startup toggle");
            ApplyStartupSetting(startup.Checked);
            Persist();
        });
        menu.Items.Add(startup);
        menu.Items.Add("설정…", null, (_, _) => QueueOpenSettingsWindow());
        menu.Items.Add("시작 가이드 다시 보기…", null, (_, _) =>
            QueueTrayDialog(OpenGettingStartedGuide));
        menu.Items.Add("설정 폴더 열기", null, (_, _) => OpenSettingsFolder());
        menu.Items.Add("진단 로그 열기", null, (_, _) => OpenDiagnosticLogs());
        menu.Items.Add("진단 묶음 만들기…", null, (_, _) => _dispatcher.Invoke(CreateDiagnosticBundle));
        menu.Items.Add("업데이트 확인…", null, (_, _) => _dispatcher.BeginInvoke(new Action(() => _ = CheckForUpdatesInteractiveAsync())));
        if (UpdateService.GetLatestRollbackDirectory() is not null)
            menu.Items.Add("마지막 앱 업데이트 되돌리기…", null, (_, _) => _dispatcher.Invoke(RollbackLastUpdate));
        menu.Items.Add("바탕화면 항목 지금 다시 검색", null, (_, _) => _dispatcher.Invoke(() => { RefreshDesktopWatchers(force: true, reconcile: false); ReorganizeDesktop(); }));
        menu.Items.Add("자동 분류 규칙 지금 적용", null, (_, _) => _dispatcher.Invoke(ApplyRulesNow));
        menu.Items.Add("설정 검사 / 복구", null, (_, _) => _dispatcher.Invoke(ValidateAndRepairConfiguration));
        menu.Items.Add("바탕화면 연결 / 레이아웃 복구", null, (_, _) => _dispatcher.Invoke(RepairDesktopAttachment));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => _dispatcher.Invoke(Exit));
        CompactTrayMenu(menu);
        UpdateTrayText();
    }

    /// <summary>
    /// Keep the ORIGINAL WinForms menu items and Click/CheckedChanged handlers.
    /// Stable command IDs win over display labels. Secondary diagnostics and
    /// startup controls remain in Settings instead of duplicating tray actions.
    /// All normal, safe-mode and calendar-only menus use the same route.
    /// </summary>
    private void CompactTrayMenu(Forms.ContextMenuStrip menu)
    {
        var original = menu.Items.Cast<Forms.ToolStripItem>()
            .Where(item => item is not Forms.ToolStripSeparator).ToArray();
        // Detach, do not Clear/clone: WinForms owns each item and we must
        // keep the original Click handlers, Enabled and Checked state.
        foreach (var item in original) menu.Items.Remove(item);
        foreach (Forms.ToolStripItem separator in menu.Items.Cast<Forms.ToolStripItem>().ToArray())
            menu.Items.Remove(separator);

        var calendar = new Forms.ToolStripMenuItem("캘린더");
        var groups = new Forms.ToolStripMenuItem("그룹 및 화면");
        var iconTools = new Forms.ToolStripMenuItem("아이콘 및 분류");
        var recovery = new Forms.ToolStripMenuItem("백업 및 복구");
        var tools = new Forms.ToolStripMenuItem("도구 및 도움말");
        var primary = new List<Forms.ToolStripItem>();
        var ending = new List<Forms.ToolStripItem>();

        foreach (var item in original)
        {
            var route = TrayMenuRoutingPolicy.Route(item.Tag as string, item.Text);
            switch (route)
            {
                case TrayMenuSection.Primary:
                    primary.Add(item);
                    break;
                case TrayMenuSection.Calendar:
                    calendar.DropDownItems.Add(item);
                    break;
                case TrayMenuSection.Groups:
                    groups.DropDownItems.Add(item);
                    break;
                case TrayMenuSection.IconTools:
                    iconTools.DropDownItems.Add(item);
                    break;
                case TrayMenuSection.Recovery:
                    recovery.DropDownItems.Add(item);
                    break;
                case TrayMenuSection.Tools:
                    tools.DropDownItems.Add(item);
                    break;
                case TrayMenuSection.SettingsOnly:
                    // Same operation exists in Settings; do not add another
                    // entry point or remove the Settings command.
                    item.Dispose();
                    break;
                case TrayMenuSection.Exit:
                    ending.Add(item);
                    break;
            }
        }
        foreach (var item in primary) menu.Items.Add(item);
        if (calendar.DropDownItems.Count > 0) menu.Items.Add(calendar);
        if (iconTools.DropDownItems.Count > 0)
            groups.DropDownItems.Add(iconTools);
        if (groups.DropDownItems.Count > 0) menu.Items.Add(groups);
        if (recovery.DropDownItems.Count > 0) menu.Items.Add(recovery);
        if (tools.DropDownItems.Count > 0) menu.Items.Add(tools);
        if (ending.Count > 0)
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            foreach (var item in ending) menu.Items.Add(item);
        }
    }

    private void UpdateTrayText()
    {
        if (_trayController is null) return;
        var status = _desktopDisplay.IsActive ? "Desktop Display" : _quickHidden ? "숨김" : _peek ? "Peek" : $"페이지 {_state.CurrentPage + 1}";
        _trayController.SetText($"CyanDeck - {status}");
    }

    // Opening a modal WPF dialog directly inside a WinForms ToolStrip click
    // keeps the ToolStrip's nested mouse-capture loop alive behind ShowDialog.
    // Return to the dispatcher first so the tray popup has closed and released
    // capture before opening the dialog (ComboBox popups and buttons need it).
    private void QueueOpenSettingsWindow() => QueueTrayDialog(OpenSettingsWindow);

    // Queue dialog creation only after a native ToolStrip menu releases its mouse capture.
    private void QueueTrayDialog(Action show)
    {
        if (_trayMenuVisible)
        {
            _pendingTrayDialog = show;
            _trayController?.CloseMenu();
        }
        else _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, show);
    }

    private void ShowCalendarDiagnostics()
    {
        var runningPath = Environment.ProcessPath ?? "알 수 없음";
        var currentState = _safeMode ? "안전 모드 (캘린더 중지)"
            : _calendar?.GetDiagnosticState() ?? "캘린더 모듈이 실행되지 않았습니다.";
        var message = $"실행 빌드: Calendar v26 여러 캘린더/저장 대상 · v41 표시 복구\n"
            + $"실행 파일: {runningPath}\n"
            + $"설정 저장값: 캘린더={_state.Settings.EnableCalendar}, CyanDeck={_state.Settings.EnableCyanDeck}\n"
            + $"실제 상태: {currentState}\n\n"
            + "창 계층에 연결됨은 실제 화면 렌더링을 보장하지 않습니다.\n"
            + "복구는 한번만 실행하세요. 실패하면 반복하지 말고 진단 로그를 확인하세요.\n"
            + "로그: %APPDATA%\\CyanDeck\\logs\\cyandeck.log";
        DiagnosticsLog.Info("Calendar diagnostics viewed: " + currentState);
        System.Windows.MessageBox.Show(message, "CyanDeck 캘린더 상태", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ResetCalendarPositionAndRepair()
    {
        if (_safeMode)
        {
            System.Windows.MessageBox.Show("안전 모드에서는 캘린더가 실행되지 않습니다. 일반 모드로 재시작하세요.",
                "CyanDeck 캘린더", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_state.Settings.EnableCalendar)
        {
            System.Windows.MessageBox.Show("현재 캘린더 활성화 설정이 저장되어 있지 않습니다. 설정에서 켜고 적용한 뒤 실행하세요.",
                "CyanDeck 캘린더", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartCalendarModule();
        var repaired = _calendar?.TryResetPositionAndRepair() == true;
        DiagnosticsLog.Info($"Calendar explicit position reset and reattach: {repaired}; {_calendar?.GetDiagnosticState() ?? "module absent"}");
        if (!repaired)
            ShowCalendarDiagnostics();
    }

    private void OpenCalendarStandaloneWindow()
    {
        if (_safeMode || !_state.Settings.EnableCalendar) return;
        StartCalendarModule();
        if (_calendar?.TryOpenStandaloneWindow() == true)
            DiagnosticsLog.Info("Calendar standalone window fallback selected from tray");
        else
            _trayController?.ShowBalloonTip(5000, "CyanDeck 캘린더",
                "일반 창 열기에 실패했습니다. 편집 중인 작업을 마친 뒤 로그를 확인하세요.",
                Forms.ToolTipIcon.Warning);
        RebuildTrayMenu();
    }

    private void RepairCalendarSurface()
    {
        if (_safeMode)
        {
            _trayController?.ShowBalloonTip(5000, "CyanDeck 캘린더",
                "안전 모드에서는 캘린더를 표시할 수 없습니다. 일반 모드로 다시 시작하세요.",
                Forms.ToolTipIcon.Warning);
            return;
        }
        StartCalendarModule();
        if (_calendar?.TryRepairVisibleSurface() == true)
            DiagnosticsLog.Info("Calendar manually restored from tray");
        else
            _trayController?.ShowBalloonTip(5000, "CyanDeck 캘린더",
                "연결 실패 또는 재시도 제한입니다. 연속 클릭하지 말고 캘린더 상태 진단에서 원인을 확인하세요.",
                Forms.ToolTipIcon.Warning);
    }

    private void OpenGettingStartedGuide()
    {
        if (_safeMode || _compatibilityReadOnly ||
            !EnsureManagedCapability("Settings.EditGeneral", "시작 가이드 설정 변경", showNotice: false))
        {
            OpenSettingsWindow();
            return;
        }
        var previous = _state.Settings.Clone();
        var guide = new FirstRunWizardWindow(previous, staged =>
        {
            _state.Settings.CopyFrom(staged);
            if (ApplySettingsChanges()) return true;
            _state.Settings.CopyFrom(previous);
            return false;
        }, firstRun: false);
        guide.ShowDialog();
    }

    private void OpenSettingsWindow()
    {
        var existing = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var settings = new SettingsWindow(_state, ApplySettingsChanges, ApplyRulesNow, ExportConfiguration, ImportConfiguration, ResetSavedMonitorLayouts, PerformAutomaticLayout, OpenMonitorContentSwapDialog, ClearIconCache, ValidateAndRepairConfiguration, OpenSettingsFolder, OpenDiagnosticLogs, CreateDiagnosticBundle, OpenSettingsAuditLog, RunSelfTest, CheckForUpdatesInteractiveAsync, RollbackLastUpdate, () => Restart(true), () => Restart(false), _safeMode, _compatibilityReadOnly, IsManagedSettingLocked, IsManagedCapabilityAllowed, GetManagedPolicyStatusText, ReloadManagedPolicyInteractive,
            AddSystemIconGroup, ResetStandaloneRecycleBinPosition, ResetCyanDeckToDefaults,
            (resetGroups, resetCalendar) => ResetVisualGeometryToDefaults(resetGroups, resetCalendar),
            ResetAllWindowGeometryToDefaults, ResetAllLocalUserData);
        DiagnosticsLog.Info("Settings dialog opening after tray/context-menu dispatch");
        settings.ShowDialog();
        DiagnosticsLog.Info("Settings dialog closed");
    }

    public void ActivateFromSecondInstance()
    {
        if (_disposed) return;
        if (_quickHidden || HasQuickHideHiddenVisualResidue())
            EnsureQuickHideVisible(QuickHideShowReason.RecoveryRepair, QuickHideInvocationSource.RecoveryRepair);
        if (_desktopDisplay.IsActive) _desktopDisplay.SetActive(false);
        RefreshDesktopTouchPageHost();
        RefreshDesktopFolderPortalDropHost();
        UpdateWindowVisibility();
        DiagnosticsLog.Info("Activated existing instance after a second launch attempt");
        OpenSettingsWindow();
    }

    private bool ApplySettingsChanges()
    {
        if (_compatibilityReadOnly) return false;
        // Commit the complete settings/fence draft before ANY observable module
        // start, stop, Explorer hook or startup registry side effect.
        try { PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true); }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Settings checkpoint failed; runtime state unchanged", ex);
            ProblemReportService.ShowOnce("layout-save", "환경설정 저장", "layout.json", ex,
                "환경설정 저장이 실패했습니다. 실행 중인 기능은 그대로 유지합니다.",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck", "layout.json"));
            return false;
        }
        if (_state.Settings.EnableCyanDeck != _desktopRuntimeStarted && !_safeMode)
        {
            AuditSettingsChanges("Settings feature switches");
            Restart(false);
            return true;
        }
        if (_state.Settings.EnableCalendar) { StartCalendarModule(); _calendar?.ApplySettings(); }
        else { _calendar?.Dispose(); _calendar = null; }
        if (!_state.Settings.EnableCyanDeck && !_safeMode)
        {
            AuditSettingsChanges("Calendar-only settings");
            ApplyStartupSetting(_state.Settings.StartWithWindows);
            RebuildTrayMenu();
            return true;
        }
        var systemGroupChanged = SynchronizeSystemGroupRecycleBin();
        var quickHideApplied = PrepareQuickHideEnabledChange(_state.Settings.EnableQuickHide, showNotice: true);
        AuditSettingsChanges("Settings window");
        _icons.Configure(_state.Settings.IconCacheMaxEntries, _state.Settings.ShowFilePreviews);
        _looseDesktopHost?.RefreshAllIcons();
        ConfigureSnapshotScreenPreviews();
        if (_safeMode)
        {
            if (_compatibilityReadOnly)
            {
                System.Windows.MessageBox.Show($"더 새로운 스키마로 만든 설정을 호환성 폴더에 보존했습니다. 덮어쓰기를 막기 위해 현재 안전 모드 세션은 읽기 전용입니다. 먼저 CyanDeck을 업데이트하거나 호환되는 백업을 가져오세요.", "CyanDeck 호환성 보호", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            Persist();
            RebuildTrayMenu();
            DiagnosticsLog.Info("Settings saved while Safe Mode was active");
            return true;
        }
        ApplyNativeDesktopVisibility();
        RefreshDesktopTouchPageHost();
        RefreshDesktopFolderPortalDropHost();
        UpdateWindowVisibility();
        ApplyStartupSetting(_state.Settings.StartWithWindows);
        _pageGesture?.UpdateSettings(_state.Settings);
        RefreshPeekCommandSurfaces();
        RefreshDesktopWatchers(force: true, reconcile: false);
        if (_state.Settings.AutoImportDesktop) ScheduleDesktopReconcile(120);
        PruneSnapshots();
        foreach (var window in _windows.ToArray()) window.RefreshFromModel();
        if (systemGroupChanged) Persist();
        if (_peek)
        {
            foreach (var window in _windows.ToArray())
            {
                window.RestoreAfterPeek();
                window.PrepareForPeek(_state.Settings.PeekRespectWorkingArea);
            }
        }
        if (_persistTimer?.IsEnabled == true)
        {
            _persistTimer.Stop();
            _persistTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_state.Settings.PersistDebounceMs, 100, 2000));
            _persistTimer.Start();
        }
        Persist();
        RebuildTrayMenu();
        return true;
    }

    private void ApplyRulesNow()
    {
        if (_safeMode) return;
        ReorganizeDesktop(forceRules: true);
    }

    private void ConfigureSnapshotScreenPreviews()
    {
        _snapshotScreenPreviews ??= new SnapshotScreenPreviewCoordinator(_store.SettingsFolder);
        _snapshotScreenPreviews.Options = new SnapshotScreenPreviewOptions
        {
            Enabled = _state.Settings.CaptureSnapshotScreenPreviews || _state.Settings.CaptureAutomaticSnapshotScreenPreviews,
            CaptureManualSnapshots = _state.Settings.CaptureSnapshotScreenPreviews,
            CaptureAutomaticDailyBackups = _state.Settings.CaptureAutomaticSnapshotScreenPreviews,
        };
    }

    private async Task CaptureSnapshotScreenPreviewAsync(DeckLayoutSnapshot snapshot, SnapshotScreenPreviewCaptureReason reason)
    {
        try
        {
            ConfigureSnapshotScreenPreviews();
            if (_snapshotScreenPreviews is null) return;
            SnapshotScreenPreviewResult result = await _snapshotScreenPreviews.CaptureAsync(snapshot.Id.ToString("D"), reason, _safeMode);
            if (result.Succeeded)
                DiagnosticsLog.Info($"Captured snapshot screen preview {result.ThumbnailWidthPx}x{result.ThumbnailHeightPx}");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Snapshot screen preview capture failed", ex);
        }
    }

    private void StartPeekCommandSurfaces()
    {
        _peekHotCorner?.Dispose();
        _peekHotCorner = new WpfPeekHotCornerHost(
            () => new PeekHotCornerOptions
            {
                Enabled = _state.Settings.PeekHotCornerEnabled,
                LeftClickAction = _state.Settings.PeekHotCornerLeftAction,
                RightClickAction = _state.Settings.PeekHotCornerRightAction,
                HitThicknessPx = _state.Settings.PeekHotCornerHitThicknessPx,
            },
            TogglePeek,
            () =>
            {
                if (_peek) SetPeek(false);
                _desktopDisplay.Toggle();
                UpdateTrayText();
                RebuildTrayMenu();
            },
            ex => DiagnosticsLog.Error("Peek hot-corner action failed", ex));

        _peekTaskbarButton?.Dispose();
        _peekTaskbarButton = new PeekTaskbarButtonHost(TogglePeek);
        RefreshPeekCommandSurfaces();
    }

    private void RefreshPeekCommandSurfaces()
    {
        if (_safeMode) return;
        try { _peekHotCorner?.Refresh(); }
        catch (Exception ex) { DiagnosticsLog.Error("Peek hot-corner refresh failed", ex); }
        try
        {
            _peekTaskbarButton?.Apply(new PeekTaskbarButtonOptions
            {
                Enabled = _state.Settings.PeekTaskbarButtonEnabled,
                CustomIconPath = string.IsNullOrWhiteSpace(_state.Settings.PeekTaskbarCustomIconPath) ? null : _state.Settings.PeekTaskbarCustomIconPath,
            });
        }
        catch (Exception ex) { DiagnosticsLog.Error("Peek taskbar button refresh failed", ex); }
    }

    public void TogglePeekFromExternalCommand()
    {
        if (_safeMode)
        {
            OpenSettingsWindow();
            return;
        }
        TogglePeek();
    }

    private void StartForegroundEventSource()
    {
        _foregroundEvents?.Dispose();
        _foregroundEvents = new WindowsForegroundEventSource();
        if (!_foregroundEvents.Start())
        {
            _foregroundEvents.Dispose();
            _foregroundEvents = null;
        }
    }

    // Foreground WinEvents are intentionally NOT used for Fence Z-order maintenance.
    // Activating another application already places that application above normal non-topmost
    // Fence windows. Rewriting layered WPF HWND Z-order on every focus transition causes a
    // visible compositor flash, especially when several Fence windows are present.
    //
    // The shared foreground source remains alive for Chameleon presentation only. Fence native
    // placement is repaired exclusively when a Fence is shown/created, Peek ends, or Explorer
    // actually rebuilds its desktop HWND hierarchy.

    private void ClearIconCache()
    {
        _icons.Clear();
        foreach (var window in _windows.Where(w => w.IsVisible).ToArray()) window.RefreshItems();
        DiagnosticsLog.Info("Icon cache cleared by user");
    }

    private void ValidateAndRepairConfiguration()
    {
        try
        {
            _store.SaveRecoveryCheckpoint(_state, "before-integrity-check");
            var report = StateIntegrityService.Repair(_state);
            if (!report.Healthy)
            {
                foreach (var window in _windows.ToArray()) window.RefreshFromModel();
                Persist();
                RebuildTrayMenu();
                DiagnosticsLog.Info($"Manual configuration integrity repair: {report.Summary}");
            }
            var details = report.Messages.Count == 0
                ? report.Summary
                : report.Summary + "\n\n" + string.Join("\n", report.Messages.Take(8).Select(m => "• " + m)) + (report.Messages.Count > 8 ? $"\n… and {report.Messages.Count - 8} more." : "");
            System.Windows.MessageBox.Show(details, "CyanDeck 설정 검사", MessageBoxButton.OK, report.Healthy ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Configuration integrity check failed", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 설정 검사 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetSavedMonitorLayouts()
    {
        _state.MonitorProfiles.Clear();
        _activeMonitorSignature = MonitorConfigurationService.GetSignature();
        Persist();
        DiagnosticsLog.Info("Cleared saved monitor layout profiles");
    }

    private void ExportConfiguration()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "CyanDeck 레이아웃 및 설정 내보내기",
                Filter = "CyanDeck 백업 (*.json)|*.json|모든 파일 (*.*)|*.*",
                FileName = $"CyanDeck-backup-{DateTime.Now:yyyyMMdd-HHmm}.json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog() != true) return;
            CaptureMonitorProfile();
            _store.Export(_state, dialog.FileName);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to export CyanDeck configuration", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 내보내기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ImportConfiguration()
    {
        if (!EnsureManagedCapability("ImportExport.Import", "레이아웃 및 설정 가져오기")) return false;
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "CyanDeck 레이아웃 및 설정 가져오기",
                Filter = "CyanDeck 백업 (*.json)|*.json|모든 파일 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() != true) return false;
            if (System.Windows.MessageBox.Show("현재 CyanDeck 레이아웃과 설정을 이 백업으로 바꾸시겠습니까?", "CyanDeck",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;

            _store.SaveRecoveryCheckpoint(_state, "before-import");
            var imported = _store.Import(dialog.FileName);
            _compatibilityReadOnly = false;
            ReplaceState(imported);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to import CyanDeck configuration", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 가져오기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Restore deleted/misconfigured built-in groups and managed Desktop item
    /// references, optionally resetting calendar-window geometry. Actual files,
    /// calendar events/accounts and all other settings remain unchanged.
    /// A durable full-state export and recovery checkpoint are mandatory.
    /// HWNDs are not moved until the relaunched process reads the new layout.
    /// </summary>
    private bool ResetAllWindowGeometryToDefaults() =>
        ResetVisualGeometryToDefaults(groups: true, calendar: true, resetGroupContent: false);

    private bool ResetVisualGeometryToDefaults(bool groups, bool calendar, bool resetGroupContent = true)
    {
        if ((!groups && !calendar) || _safeMode || _compatibilityReadOnly ||
            _disposed || (_displayTransition?.IsStableForUserLayoutAction == false) ||
            _monitorContentSwapInProgress) return false;
        if (groups && !EnsureManagedCapability("Layout.MoveResizeGroups", "그룹 위치 초기화"))
            return false;
        if (calendar && !EnsureManagedCapability("Settings.EditGeneral", "캘린더 위치 초기화"))
            return false;

        var what = !resetGroupContent ? "모든 그룹 및 캘린더 창 위치·크기"
            : groups && calendar ? "그룹·아이콘 초기 구성 및 캘린더 위치·크기"
            : groups ? "그룹·아이콘 초기 구성" : "캘린더 위치·크기";
        if (System.Windows.MessageBox.Show(
            $"{what}를 초기 상태로 복원하시겠습니까?\n\n" +
            (groups && resetGroupContent
                ? "앱·작업·미디어·Windows 기본 아이콘 그룹을 다시 만들고 바탕화면 항목을 재검색합니다.\n" +
                  "사용자 지정 그룹·탭·분류 규칙·폴더 포털 배치는 기본 구성으로 바뀝니다.\n"
                : groups ? "모든 페이지의 그룹 창 위치·크기만 초기화하고 그룹 내용은 그대로 보존합니다.\n" : "") +
            "Windows의 실제 파일·폴더는 이동·삭제하지 않습니다. 캘린더 일정·계정 및 나머지 프로그램 설정은 유지합니다.\n" +
            "기존 상태를 전체 백업한 뒤 적용하고 CyanDeck을 재시작합니다.\n" +
            "설정 창에서 아직 적용하지 않은 편집 내용은 저장되지 않습니다.",
            "CyanDeck — 위치 및 크기 초기화", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return false;

        string? backup = null;
        List<DeckFence>? previousGroups = null;
        List<DeckItem>? previousLooseIcons = null;
        List<string>? previousUngroupedPaths = null;
        List<DeckMonitorLayoutProfile>? monitorProfiles = null;
        List<Action>? restoreGroupGeometry = null;
        var oldCalendarLeft = _state.Settings.CalendarLeft;
        var oldCalendarTop = _state.Settings.CalendarTop;
        var oldCalendarWidth = _state.Settings.CalendarWidth;
        var oldCalendarHeight = _state.Settings.CalendarHeight;
        var oldPage = _state.CurrentPage;
        try
        {
            // Include the latest on-screen positions in the exported rollback.
            if (_desktopRuntimeStarted)
                CaptureMonitorProfile();
            var backupDir = Path.Combine(_store.SettingsFolder, "reset-backups");
            Directory.CreateDirectory(backupDir);
            backup = Path.Combine(backupDir,
                $"CyanDeck-before-position-reset-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            _store.Export(_state, backup);
            if (!File.Exists(backup) || new FileInfo(backup).Length == 0)
                throw new IOException("원본 레이아웃 백업을 생성하지 못했습니다.");
            _store.Import(backup);
            var checkpoint = _store.SaveRecoveryCheckpoint(_state, "before-geometry-reset");
            if (string.IsNullOrWhiteSpace(checkpoint) || !File.Exists(checkpoint) ||
                new FileInfo(checkpoint).Length == 0)
                throw new IOException("레이아웃 복구 체크포인트를 만들지 못했습니다.");

            previousGroups = _state.Fences;
            previousLooseIcons = _state.LooseDesktopItems;
            previousUngroupedPaths = _state.UngroupedDesktopPaths;
            monitorProfiles = _state.MonitorProfiles.ToList();
            if (groups && !resetGroupContent)
            {
                restoreGroupGeometry = _state.Fences.Select(f =>
                {
                    var x = f.X; var y = f.Y; var w = f.Width; var h = f.Height;
                    var expanded = f.ExpandedHeight; var rolled = f.RolledUp;
                    var dock = f.RollupDockEdge;
                    return (Action)(() =>
                    {
                        f.X = x; f.Y = y; f.Width = w; f.Height = h;
                        f.ExpandedHeight = expanded; f.RolledUp = rolled;
                        f.RollupDockEdge = dock;
                    });
                }).ToList();
                var work = SystemParameters.WorkArea;
                var plan = InitialGeometryResetPolicy.Plan(
                    new MonitorGroupRecoveryPlanner.WorkArea(work.Left, work.Top, work.Width, work.Height),
                    _state.Fences.Select(f => new InitialGeometryResetPolicy.ExistingGroup(
                        f.Id, f.PageIndex, f.ShowOnAllPages)).ToArray(),
                    _state.Settings.GroupSnapGap);
                var byId = _state.Fences.ToDictionary(f => f.Id);
                foreach (var placement in plan.Placements)
                {
                    var f = byId[placement.Id];
                    f.X = placement.X; f.Y = placement.Y;
                    f.Width = placement.Width; f.Height = placement.Height;
                    f.ExpandedHeight = placement.Height;
                    f.RolledUp = false;
                    f.RollupDockEdge = DeckRollupDockEdge.None;
                }
                _state.MonitorProfiles.Clear();
                DiagnosticsLog.Info($"All-window geometry restore planned: " +
                    $"groups={plan.Placements.Count}; pages={_state.Fences.Select(f => f.PageIndex).Distinct().Count()}; " +
                    $"overlap-unavoidable={plan.OverlapUnavoidable}");
            }
            if (groups && resetGroupContent)
            {
                // Re-create the ORIGINAL built-in groups, their tabs/rules,
                // dimensions and supported Windows virtual icons. Do not reset
                // application settings or touch files on disk.
                CreateDefaultLayout();

                // Every actual Desktop/Public Desktop item must become visible
                // again even when the user previously removed it from a group
                // or pinned it outside one. Only direct-child desktop items
                // are reclassified; other loose references remain untouched.
                var roots = DesktopDirectoryService.GetRoots();
                _state.LooseDesktopItems = previousLooseIcons
                    .Where(item => !DesktopDirectoryService.IsDirectChildOfAnyRoot(item.Path, roots))
                    .ToList();
                var recovered = 0;
                foreach (var path in RuleEngine.EnumerateDesktopItems()
                    .Select(DesktopDirectoryService.Normalize)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var tab = RuleEngine.PickTab(_state, path, includeRules: true);
                    if (tab is null) continue;
                    tab.Items.Add(new DeckItem { Path = path });
                    recovered++;
                }
                SyncLegacyUngroupedPaths();
                _state.CurrentPage = 0;
                // Old per-monitor profiles would otherwise reapply deleted
                // groups/positions on reconnection.
                _state.MonitorProfiles.Clear();
                DiagnosticsLog.Info($"Default group and desktop icon references restored: " +
                    $"groups={_state.Fences.Count}; desktop-items={recovered}; " +
                    $"other loose icons preserved={_state.LooseDesktopItems.Count}");
            }
            if (calendar)
            {
                var defaults = InitialGeometryResetPolicy.CalendarDefaults;
                _state.Settings.CalendarLeft = defaults.Left;
                _state.Settings.CalendarTop = defaults.Top;
                _state.Settings.CalendarWidth = defaults.Width;
                _state.Settings.CalendarHeight = defaults.Height;
            }
            // Old HWNDs still reference pre-reset models until restart.
            _geometryResetPendingRestart = true;
            PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true);
            DiagnosticsLog.Info($"Initial group/calendar reset durably saved: groups={groups}; " +
                $"group-content-rebuilt={resetGroupContent && groups}; " +
                $"calendar={calendar}; backup={backup}; checkpoint={checkpoint}");
            System.Windows.MessageBox.Show(
                $"선택한 위치·크기를 초기화했습니다.\n\n" +
                $"복구 백업: {backup}\n프로그램을 다시 시작합니다.",
                "CyanDeck 위치 초기화 완료", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Restart(false);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Initial group/calendar reset failed", ex);
            // Restore the live models so a later auto-save cannot silently
            // commit a partially reset desktop.
            _geometryResetPendingRestart = false;
            restoreGroupGeometry?.ForEach(restore => restore());
            if (previousGroups is not null) _state.Fences = previousGroups;
            if (previousLooseIcons is not null) _state.LooseDesktopItems = previousLooseIcons;
            if (previousUngroupedPaths is not null) _state.UngroupedDesktopPaths = previousUngroupedPaths;
            if (monitorProfiles is not null)
                _state.MonitorProfiles = monitorProfiles;
            _state.CurrentPage = oldPage;
            _state.Settings.CalendarLeft = oldCalendarLeft;
            _state.Settings.CalendarTop = oldCalendarTop;
            _state.Settings.CalendarWidth = oldCalendarWidth;
            _state.Settings.CalendarHeight = oldCalendarHeight;
            _persistTimer?.Stop();
            try { PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true); }
            catch (Exception rollbackEx) { DiagnosticsLog.Error("Geometry rollback save failed; exported backup still available", rollbackEx); }
            System.Windows.MessageBox.Show(
                $"위치 초기화에 실패했습니다. 기존 배치로 복원했습니다.\n" +
                $"백업: {backup ?? "생성 실패"}\n\n{ex.Message}",
                "CyanDeck 위치 초기화 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Factory reset touches CyanDeck layout/settings only. Never delete desktop
    /// files, calendar-events.json, calendar-preferences.json, Google tokens or OAuth
    /// credentials. Back up the old state BEFORE changing any in-memory layout.
    /// </summary>
    private bool ResetCyanDeckToDefaults()
    {
        if (_compatibilityReadOnly || _safeMode) return false;
        if (!EnsureManagedCapability("ImportExport.Import", "프로그램 초기화") ||
            !EnsureManagedCapability("Settings.EditGeneral", "프로그램 초기화")) return false;
        var confirm = System.Windows.MessageBox.Show(
            "CyanDeck 그룹, 바탕화면 배치, 프로그램 환경설정을 기본값으로 초기화하시겠습니까?\n\n" +
            "현재 레이아웃·설정은 복원 가능한 JSON 파일로 백업합니다.\n" +
            "실제 파일/폴더, 캘린더 일정, Google 로그인 및 인증 정보는 삭제하지 않습니다.\n\n" +
            "초기화 완료 후 CyanDeck이 다시 시작됩니다.",
            "CyanDeck — 프로그램 초기화", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return false;
        var explicitConsent = TextPrompt.Show(Application.Current.Windows.OfType<SettingsWindow>().First(), "프로그램 초기화 확인", "계속하려면 '초기화'를 입력하세요.", "");
        if (!string.Equals(explicitConsent?.Trim(), "초기화", StringComparison.Ordinal)) return false;

        string? backup = null;
        var resetStarted = false;
        try
        {
            var dir = Path.Combine(_store.SettingsFolder, "reset-backups");
            Directory.CreateDirectory(dir);
            backup = Path.Combine(dir, $"CyanDeck-before-reset-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            // Fail closed: if export/checkpoint cannot be made, do not reset anything.
            _store.Export(_state, backup);
            if (!File.Exists(backup) || new FileInfo(backup).Length == 0)
                throw new IOException("원본 설정 백업을 생성하지 못했습니다.");
            // A readable, restorable backup is required before any live state is replaced.
            _store.Import(backup);
            var checkpoint = _store.SaveRecoveryCheckpoint(_state, "before-factory-reset");
            if (string.IsNullOrWhiteSpace(checkpoint) || !File.Exists(checkpoint) ||
                new FileInfo(checkpoint).Length == 0)
                throw new IOException("설정 복구 체크포인트를 생성하지 못했습니다.");
            var defaults = new DeckState();
            // The OAuth client ID may be user-supplied. Keep it so that the Calendar
            // module still connects to its existing authorized account after restart.
            defaults.Settings.GoogleCalendarClientId = _state.Settings.GoogleCalendarClientId;
            resetStarted = true;
            ReplaceState(defaults);
            // A reset is not successful until the new layout is durably committed.
            PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true);
            DiagnosticsLog.Info($"Factory reset completed, backup available at {backup}");
            System.Windows.MessageBox.Show(
                $"프로그램 설정과 그룹을 초기화했습니다.\n\n복구 백업: {backup}\n\n" +
                "캘린더 일정과 Google 인증 정보, 실제 파일·폴더는 그대로 유지됩니다.\n프로그램을 다시 시작합니다.",
                "CyanDeck 초기화 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            Restart(false);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Factory reset failed", ex);
            // On failure, attempt to recover the prior settings from the durable backup.
            if (resetStarted && backup is not null && File.Exists(backup))
            {
                try
                {
                    ReplaceState(_store.Import(backup));
                    PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true);
                }
                catch (Exception recoveryEx)
                {
                    DiagnosticsLog.Error("Factory reset rollback failed; exported backup retained", recoveryEx);
                }
            }
            System.Windows.MessageBox.Show(
                $"초기화에 실패했습니다. 이전 설정 백업은 보존됩니다.\n{ex.Message}",
                "CyanDeck 초기화 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Explicitly requested destructive LOCAL profile reset. Unlike the normal
    /// settings factory reset, clears local calendar events and Google tokens.
    /// It never calls Google APIs or deletes original Windows Desktop files.
    /// </summary>
    private bool ResetAllLocalUserData()
    {
        if (_safeMode || _compatibilityReadOnly || _disposed ||
            _monitorContentSwapInProgress ||
            (_displayTransition?.IsStableForUserLayoutAction == false) ||
            !EnsureManagedCapability("ImportExport.Import", "전체 로컬 데이터 초기화") ||
            !EnsureManagedCapability("Settings.EditGeneral", "전체 로컬 데이터 초기화"))
            return false;
        if (UpdateService.HasPendingRecovery())
        {
            System.Windows.MessageBox.Show("미완료 업데이트 복구가 있습니다. 먼저 복구를 완료하세요.",
                "CyanDeck 완전 초기화", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (System.Windows.MessageBox.Show(
            "이 컴퓨터에 저장된 CyanDeck 사용자 데이터를 모두 삭제하시겠습니까?\n\n" +
            "그룹/설정, 로컬 일정, Google 로그인 토큰/선택, 백업·스냅샷·로그를 삭제합니다.\n" +
            "Google 서버 일정, Windows 바탕화면 원본 파일과 설치된 프로그램은 삭제하지 않습니다.\n\n" +
            "자동 백업 없이 영구 삭제합니다. 필요한 자료는 먼저 다른 폴더에 내보내세요.",
            "CyanDeck — 모든 로컬 데이터 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return false;
        var owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (owner is null) return false;
        var confirmation = TextPrompt.Show(owner, "모든 사용자 데이터 삭제 확인",
            "계속하려면 '모든 데이터 삭제'를 정확히 입력하세요.", "");
        if (!string.Equals(confirmation, "모든 데이터 삭제", StringComparison.Ordinal)) return false;

        string? ticket = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                throw new InvalidOperationException("재시작 실행 파일 경로를 확인하지 못했습니다.");
            ticket = CompleteLocalResetService.CreateRequest(DataRootPaths.ActiveRoaming);
            var args = $"--wait-for-pid {Environment.ProcessId} --complete-local-reset {ticket}";
            using var child = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            if (child is null) throw new IOException("초기화 전용 프로세스를 실행하지 못했습니다.");
            Exit();
            return true;
        }
        catch (Exception ex)
        {
            if (ticket is not null)
                CompleteLocalResetService.CancelRequest(DataRootPaths.ActiveRoaming, ticket);
            DiagnosticsLog.Error("Complete local reset could not be started", ex);
            System.Windows.MessageBox.Show("완전 초기화를 시작하지 못했습니다. 기존 데이터는 유지됩니다.\n" + ex.Message,
                "CyanDeck 완전 초기화 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ReplaceState(DeckState imported)
    {
        _desktopDisplay.SetActive(false);
        if (_peek) SetPeek(false);
        foreach (var window in _windows.ToArray()) window.CloseFromController();
        _windows.Clear();

        _state = imported;
        _settingsAuditBaseline = SettingsAuditService.Capture(_state.Settings);
        _quickHideConfiguredEnabled = _state.Settings.EnableQuickHide;
        ReapplyManagedPolicyAfterStateReplacement();
        _activeMonitorSignature = MonitorConfigurationService.GetSignature();
        if (_state.Fences.Count == 0) CreateDefaultLayout();
        if (_safeMode)
        {
            _state.CurrentPage = Math.Clamp(_state.CurrentPage, 0, GetMaxPage());
            Persist();
            _store.RefreshSessionBaseline(_state);
            RebuildTrayMenu();
            DiagnosticsLog.Info("Imported configuration while Safe Mode was active; desktop integration remains disabled until restart");
            return;
        }
        ApplyInitialLayoutAdjustment();
        _state.CurrentPage = Math.Clamp(_state.CurrentPage, 0, GetMaxPage());
        ResetDesktopFirstSeenGate();
        if (_state.Settings.AutoImportDesktop) ReorganizeDesktop();

        foreach (var fence in _state.Fences) CreateWindow(fence);
        UpdateWindowVisibility();
        _quickHidden = false;
        ApplyNativeDesktopVisibility();
        RefreshDesktopTouchPageHost();
        RefreshDesktopFolderPortalDropHost();
        RefreshLooseDesktopIconHost();
        ApplyStartupSetting(_state.Settings.StartWithWindows);
        _pageGesture?.UpdateSettings(_state.Settings);
        RefreshDesktopWatchers(force: true, reconcile: false);
        if (_state.Settings.AutoImportDesktop) ScheduleDesktopReconcile(120);
        Persist();
        _store.RefreshSessionBaseline(_state);
        RebuildTrayMenu();
    }

    private void OpenSettingsFolder()
    {
        Directory.CreateDirectory(_store.SettingsFolder);
        Process.Start(new ProcessStartInfo(_store.SettingsFolder) { UseShellExecute = true });
    }

    private static void OpenDiagnosticLogs()
    {
        Directory.CreateDirectory(DiagnosticsLog.LogFolder);
        Process.Start(new ProcessStartInfo(DiagnosticsLog.LogFolder) { UseShellExecute = true });
    }

    private void CreateDiagnosticBundle()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "CyanDeck 진단 번들 만들기",
                Filter = "ZIP archive (*.zip)|*.zip",
                FileName = $"CyanDeck-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
                AddExtension = true,
                DefaultExt = ".zip"
            };
            if (dialog.ShowDialog() != true) return;
            DiagnosticBundleService.Create(dialog.FileName, _state, _icons.GetStats(), _safeMode, _managedPolicy?.GetDiagnosticSummary(), _quickHideStartupStatus, _quickHideStartupShowReason);
            DiagnosticsLog.Info($"Diagnostic bundle created: {dialog.FileName}");
            System.Windows.MessageBox.Show("진단 번들을 만들었습니다. 로그와 환경 요약은 포함하지만 레이아웃 JSON과 바탕화면 항목 경로는 포함하지 않습니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to create diagnostic bundle", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 진단 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ScheduleAutomaticUpdateCheck()
    {
        // A successful, failed, timed-out, or skipped startup preflight must not
        // initiate a second hidden auto-install after the desktop is visible.
        if (_suppressStartupUpdateCheck) return;
        if (!_state.Settings.EnableAutomaticUpdateChecks && !_state.Settings.InstallUpdatesAutomatically) return;
        // The startup check happens once per process, not once per day.
        // Never auto-restart from Safe Mode or while an update requires recovery.
        if (_safeMode || _compatibilityReadOnly || UpdateService.HasPendingRecovery()) return;
        if (string.IsNullOrWhiteSpace(UpdateService.ResolveManifestSource(_state.Settings.UpdateManifestSource, _state.Settings.EnableBetaUpdateChannel))) return;
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = CheckForUpdatesAsync(interactive: false)));
    }

    private Task CheckForUpdatesInteractiveAsync() => CheckForUpdatesAsync(interactive: true);

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_automaticUpdateInProgress) return;
        _automaticUpdateInProgress = true;
        try
        {
            // Record attempts as well as successful checks so an unreachable automatic
            // channel cannot trigger a network request on every application start.
            _state.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            Persist();
            var channelSource = UpdateService.ResolveManifestSource(_state.Settings.UpdateManifestSource, _state.Settings.EnableBetaUpdateChannel);
            var check = await UpdateService.CheckAsync(channelSource, _state.Settings.RequireSignedUpdateManifests, _state.Settings.UpdatePublicKeyPath);
            DiagnosticsLog.Info($"Update check completed: channel={(_state.Settings.EnableBetaUpdateChannel ? "beta" : "stable")}, current={check.CurrentVersion}, latest={check.LatestVersion}, available={check.UpdateAvailable}, signatureVerified={check.SignatureVerified}");
            if (!check.UpdateAvailable)
            {
                if (interactive)
                    System.Windows.MessageBox.Show($"CyanDeck {check.CurrentVersion.Major}.{check.CurrentVersion.Minor}.{check.CurrentVersion.Build}은(는) 최신 버전입니다.", "CyanDeck 업데이트", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!interactive)
            {
                if (!_state.Settings.InstallUpdatesAutomatically)
                {
                    _trayController?.ShowBalloonTip(6000, "CyanDeck 업데이트 사용 가능", $"버전 {check.LatestVersion}을(를) 사용할 수 있습니다. 설정에서 설치하세요.", Forms.ToolTipIcon.Info);
                    return;
                }
                // Background installation requires all existing recovery safeguards.
                if (_safeMode || _compatibilityReadOnly || UpdateService.HasPendingRecovery()) return;
                var verifiedPackage = await UpdateService.StagePackageAsync(check);
                _store.SaveRecoveryCheckpoint(_state, $"before-github-auto-update-{check.LatestVersion}");
                if (!UpdateService.TryLaunchInstall(verifiedPackage, check.Manifest.Sha256, Environment.ProcessId, relaunch: true))
                    throw new InvalidOperationException("Unable to launch the verified GitHub update installer.");
                DiagnosticsLog.Info($"Verified GitHub update installer launched; restarting for version {check.LatestVersion}.");
                Exit();
                return;
            }

            var notes = string.IsNullOrWhiteSpace(check.Manifest.Notes) ? "" : $"\n\n{check.Manifest.Notes}";
            if (System.Windows.MessageBox.Show($"CyanDeck {check.LatestVersion}을(를) 사용할 수 있습니다.\n\n지금 업데이트를 다운로드하고 검증하시겠습니까?{notes}", "CyanDeck 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // Installing while an earlier update awaits rollback/recovery could
            // overwrite its checkpoint or corrupt the in-place application ZIP.
            if (UpdateService.HasPendingRecovery())
                throw new InvalidOperationException("이전 업데이트 복구가 완료되지 않았습니다. 앱을 정상적으로 다시 시작하거나 업데이트 복구 후 다시 시도하세요.");
            var staged = await UpdateService.StagePackageAsync(check);
            _store.SaveRecoveryCheckpoint(_state, $"before-update-{check.LatestVersion}");
            var verification = "업데이트 매니페스트 RSA-PSS 서명과 패키지 SHA-256을 검증했습니다.";
            if (System.Windows.MessageBox.Show($"{verification}\n\n지금 CyanDeck {check.LatestVersion}을(를) 설치하시겠습니까? CyanDeck이 다시 시작됩니다.", "CyanDeck 업데이트 준비 완료", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (!UpdateService.TryLaunchInstall(staged, check.Manifest.Sha256, Environment.ProcessId, relaunch: true))
                throw new InvalidOperationException("검증된 업데이트 설치 프로그램을 시작하지 못했습니다.");
            Exit();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("CyanDeck update check/install failed", ex);
            if (interactive)
                System.Windows.MessageBox.Show(ex.Message, "CyanDeck 업데이트 확인 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _automaticUpdateInProgress = false;
        }
    }

    private void RollbackLastUpdate()
    {
        try
        {
            var rollback = UpdateService.GetLatestRollbackDirectory();
            if (string.IsNullOrWhiteSpace(rollback))
            {
                System.Windows.MessageBox.Show("되돌릴 수 있는 이전 프로그램 버전이 없습니다.", "CyanDeck 버전 되돌리기", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (System.Windows.MessageBox.Show("마지막 CyanDeck 업데이트 전에 저장한 프로그램 파일로 되돌리시겠습니까? %APPDATA%의 레이아웃과 설정은 교체되지 않습니다.", "CyanDeck 버전 되돌리기", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _store.SaveRecoveryCheckpoint(_state, "before-app-rollback");
            if (!UpdateService.TryLaunchRollback(Environment.ProcessId, relaunch: true))
                throw new InvalidOperationException("사용 가능한 되돌리기 패키지가 없습니다.");
            Exit();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("CyanDeck application rollback failed", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 버전 되돌리기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RepairDesktopAttachment()
    {
        try
        {
            if (_peek) SetPeek(false);
            _host.PrepareShell();
            foreach (var window in _windows.ToArray())
            {
                _host.EnsureAttached(window);
                window.EnsureVisibleOnMonitor();
                window.RefreshFromModel();
            }
            // The legacy desktop repair command must also include the calendar.
            // Otherwise it can report success while the enabled calendar stays hidden.
            if (_state.Settings.EnableCalendar && !_safeMode)
                _calendar?.TryRepairVisibleSurface();
            if (_quickHidden || HasQuickHideHiddenVisualResidue())
                EnsureQuickHideVisible(QuickHideShowReason.RecoveryRepair, QuickHideInvocationSource.RecoveryRepair);
            ApplyNativeDesktopVisibility();
            RefreshDesktopWatchers(force: true, reconcile: false);
            if (_state.Settings.AutoImportDesktop) ReorganizeDesktop();
            UpdateWindowVisibility();
            Persist();
            if (_quickHideStartupStatus is QuickHideStartupApplyStatus.DeferredDesktopNotReady or QuickHideStartupApplyStatus.FailedRetryable)
                _ = TryApplyStartupQuickHideNormalizationAsync();
            DiagnosticsLog.Info("Manual desktop attachment/layout repair completed");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Manual desktop repair failed", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 바탕화면 연결 복구 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private LinkedIconSpacingCoordinator GetLinkedIconSpacingCoordinator()
        => _linkedIconSpacingCoordinator ??= new LinkedIconSpacingCoordinator(this);

    private IconSpacing ResolveEffectiveIconSpacing(DeckFence fence, DeckTab tab, ItemSurfaceLayoutKind surfaceKind)
        => GetLinkedIconSpacingCoordinator().ResolveEffectiveSpacing(new LinkedSpacingSurfaceSnapshot(
            $"{fence.Id:D}:{tab.Id:D}",
            surfaceKind,
            new IconSpacing(fence.IconSpacingX, fence.IconSpacingY)));

    bool ILinkedIconSpacingHost.MasterEnabled => !_disposed;
    bool ILinkedIconSpacingHost.IsSafeMode => _safeMode;
    bool ILinkedIconSpacingHost.CanEditLayout => !_compatibilityReadOnly
        && IsManagedCapabilityAllowed(LinkedIconSpacingIntegrationContract.ManagedCapability)
        && !IsManagedSettingLocked(LinkedIconSpacingIntegrationContract.SettingKey);
    bool ILinkedIconSpacingHost.UseSameSpacingInsideAndOutsideGroups => _state.Settings.UseSameSpacingInsideAndOutsideGroups;
    IconSpacing ILinkedIconSpacingHost.GetLooseDesktopIconSpacing() => LooseDesktopIconLayoutProfile.Spacing;

    IEnumerable<LinkedSpacingSurfaceSnapshot> ILinkedIconSpacingHost.EnumerateGroupSurfaces()
    {
        foreach (var fence in _state.Fences)
        {
            if (fence.Tabs.Count == 0) continue;
            var index = Math.Clamp(fence.SelectedTabIndex, 0, fence.Tabs.Count - 1);
            var tab = fence.Tabs[index];
            var kind = tab.Kind == DeckTabKind.Collection && fence.IconLayoutMode == DeckIconLayoutMode.Free && tab.ViewMode == DeckItemViewMode.Grid
                ? ItemSurfaceLayoutKind.FreePlacement
                : tab.ViewMode switch
                {
                    DeckItemViewMode.List => ItemSurfaceLayoutKind.List,
                    DeckItemViewMode.Details => ItemSurfaceLayoutKind.Details,
                    _ => ItemSurfaceLayoutKind.Grid
                };
            yield return new LinkedSpacingSurfaceSnapshot(
                $"{fence.Id:D}:{tab.Id:D}",
                kind,
                new IconSpacing(fence.IconSpacingX, fence.IconSpacingY));
        }
    }

    void ILinkedIconSpacingHost.SetUseSameSpacingInsideAndOutsideGroups(bool value)
        => _state.Settings.UseSameSpacingInsideAndOutsideGroups = value;

    void ILinkedIconSpacingHost.RefreshGridSurface(string surfaceId)
    {
        var separator = surfaceId.IndexOf(':');
        if (separator <= 0 || !Guid.TryParse(surfaceId[..separator], out var fenceId)) return;
        _windows.FirstOrDefault(w => w.Model.Id == fenceId)?.RefreshLinkedIconSpacing();
    }

    void ILinkedIconSpacingHost.RefreshSpacingEditorState()
        => RefreshOpenManagedPolicyUi();

    void ILinkedIconSpacingHost.SaveState() => Persist();

    private static void ApplyStartupSetting(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe)) key.SetValue("CyanDeck", $"\"{exe}\"");
            }
            else key.DeleteValue("CyanDeck", false);
        }
        catch { }
    }

    private TimeSpan GetPersistDelay()
    {
        // Exponential backoff for external file locks: 2, 4, 8, 16, 30s.
        // A new UI change must not reset an active retry back to the normal
        // 450ms debounce, or a persistent scanner lock could spin on the UI thread.
        if (_persistStorageRetryCount > 0)
        {
            var seconds = Math.Min(30, 1 << Math.Min(_persistStorageRetryCount, 5));
            return TimeSpan.FromSeconds(seconds);
        }
        return TimeSpan.FromMilliseconds(Math.Clamp(_state.Settings.PersistDebounceMs, 100, 2000));
    }

    private void Persist()
    {
        if (_disposed || _compatibilityReadOnly) return;
        _persistDirty = true;
        _persistTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        if (_persistStorageRetryCount > 0 && _persistTimer.IsEnabled)
            return; // Preserve the pending backoff deadline during frequent UI changes.
        _persistTimer.Stop();
        _persistTimer.Interval = GetPersistDelay();
        _persistTimer.Tick -= PersistTimer_Tick;
        _persistTimer.Tick += PersistTimer_Tick;
        _persistTimer.Start();
    }

    private void PersistTimer_Tick(object? sender, EventArgs e)
    {
        _persistTimer?.Stop();
        try
        {
            PersistNow();
        }
        catch (IOException ex)
        {
            DeferPersistAfterFileError(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            DeferPersistAfterFileError(ex);
        }
    }

    private void DeferPersistAfterFileError(Exception ex)
    {
        if (_disposed || _compatibilityReadOnly) return;
        _persistDirty = true; // Never acknowledge a save that did not succeed.
        _persistStorageRetryCount = Math.Min(_persistStorageRetryCount + 1, 6);
        if (_persistStorageRetryCount is 1 or 3 or 6)
            DiagnosticsLog.Warn($"Layout auto-save deferred: {ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}; retry in {GetPersistDelay().TotalSeconds:0}s. Current layout remains in memory.");
        if (_persistStorageRetryCount == 3)
        {
            // Report only after repeated failed checkpoints, not on an ephemeral
            // Explorer/antivirus lock. Retry continues without a blocking dialog.
            var layoutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck", "layout.json");
            ProblemReportService.ShowOnce("layout-save", "바탕화면 레이아웃 자동 저장", "layout.json", ex,
                "자동 저장을 연속 3회 실패했습니다. 메모리의 변경 사항을 유지하며 저장을 재시도합니다.",
                layoutPath, layoutPath + ".tmp");
            _trayController?.ShowBalloonTip(5000, "CyanDeck 저장 오류",
                "진단 정보가 담긴 별도 창을 열었습니다. 창에서 복사하여 개발자에게 전달해 주세요.",
                Forms.ToolTipIcon.Warning);
        }
        // Force-save paths (including first run) can be invoked before Persist()
        // created a timer. In that case, a failed save must still be retried.
        _persistTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _persistTimer.Tick -= PersistTimer_Tick;
        _persistTimer.Tick += PersistTimer_Tick;
        _persistTimer.Stop();
        _persistTimer.Interval = GetPersistDelay();
        _persistTimer.Start();
    }

    private void PersistNow(bool force = false, bool bypassDisplayTransition = false, bool requireDurableSave = false)
    {
        if (_compatibilityReadOnly) return;
        if (!force && !_persistDirty) return;
        if (!bypassDisplayTransition && _displayTransition?.DeferLayoutSaveIfTransitioning() == true)
        {
            _persistDirty = true;
            _displayTransitionDeferredSaveCount++;
            return;
        }
        CaptureMonitorProfile();
        if (_recycleBinDesktopItem is not null)
        {
            _state.Settings.RecycleBinPositionX = _recycleBinDesktopItem.PositionX;
            _state.Settings.RecycleBinPositionY = _recycleBinDesktopItem.PositionY;
        }
        var addedAutomaticSnapshot = EnsureAutomaticDailySnapshot();
        try
        {
            _store.Save(_state);
        }
        catch (IOException ex)
        {
            // File.Replace, File.Move, and staging can all fail while Explorer,
            // an indexer, or a scanner temporarily holds a file open. None of
            // the callers of a forced save should terminate the WPF process.
            DeferPersistAfterFileError(ex);
            if (requireDurableSave) throw; // Transactional callers must know the checkpoint failed.
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Windows may report a temporary exclusive-handle conflict as
            // ERROR_ACCESS_DENIED instead of ERROR_SHARING_VIOLATION.
            DeferPersistAfterFileError(ex);
            if (requireDurableSave) throw;
            return;
        }
        _persistDirty = false;
        if (_persistStorageRetryCount > 0)
        {
            DiagnosticsLog.Info("Layout auto-save recovered after file contention.");
            _persistStorageRetryCount = 0;
            ProblemReportService.Reset("layout-save");
        }
        if (addedAutomaticSnapshot && _trayController is not null) RebuildTrayMenu();
    }

    public void MarkAbnormalTermination() => _abnormalTermination = true;

    private bool RestoreRecoveryCheckpoint(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                System.Windows.MessageBox.Show("복구 체크포인트를 더 이상 사용할 수 없습니다.", "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            var targetState = _store.Import(path);
            var diff = RecoveryDiffService.Compare(_state, targetState);
            var prompt = $"이 CyanDeck 복구 체크포인트를 복원하시겠습니까? 먼저 현재 상태를 체크포인트로 저장합니다.\n\n{diff.ToDisplayString()}";
            if (System.Windows.MessageBox.Show(prompt, "CyanDeck 복구", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;
            _store.SaveRecoveryCheckpoint(_state, "before-recovery-restore");
            ReplaceState(targetState);
            _uncleanRecoveryPath = null;
            DiagnosticsLog.Info($"Restored recovery checkpoint {path}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to restore recovery checkpoint", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 복구 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void AuditSettingsChanges(string source)
    {
        SettingsAuditService.RecordChanges(_settingsAuditBaseline, _state.Settings, source);
        _settingsAuditBaseline = SettingsAuditService.Capture(_state.Settings);
    }

    private static void OpenSettingsAuditLog()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsLog.LogFolder);
            if (!File.Exists(SettingsAuditService.AuditPath)) File.WriteAllText(SettingsAuditService.AuditPath, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SettingsAuditService.AuditPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to open settings audit log", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunSelfTest()
    {
        try
        {
            var report = SelfTestService.Run(_state, _safeMode);
            System.Windows.MessageBox.Show(report.ToDisplayString(), "CyanDeck 자체 검사", MessageBoxButton.OK, report.Passed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("CyanDeck 자체 검사 실패", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 자체 검사 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Restart(bool safeMode)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var args = $"--wait-for-pid {Environment.ProcessId}" + (safeMode ? " --safe-mode" : " --ignore-crash-loop");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            Exit();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to restart CyanDeck", ex);
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck 다시 시작 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Exit()
    {
        _nativeIconDisplayRepairTimer?.Stop();
        _shellRecoveryScheduler?.Dispose();
        _shellRecoveryScheduler = null;
        _calendar?.Dispose();
        _calendar = null;
        _managedPolicyWatcher?.Dispose();
        _managedPolicyWatcher = null;
        _quickHideStartupCoordinator?.Dispose();
        _quickHideStartupCoordinator = null;
        _displayTransition?.Dispose();
        _displayTransition = null;
        _looseDesktopHost?.Dispose();
        _looseDesktopHost = null;
        _touchPageHost?.Dispose();
        _touchPageHost = null;
        _folderPortalDropHost?.Dispose();
        _desktopCreationPreviewHost?.Dispose();
        _desktopCreationMenu?.Dispose();
        _folderPortalDropHost = null;
        if (!_safeMode && _desktopRuntimeStarted) _host.SetNativeDesktopIconsVisible(true);
        foreach (var w in _windows.ToArray()) w.CloseFromController();
        _trayController?.Hide();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _calendar?.Dispose();
        _calendar = null;
        foreach (var recovery in _showDesktopRecoveries.Values) recovery.Dispose();
        _showDesktopRecoveries.Clear();
        try { PersistNow(force: true, bypassDisplayTransition: true); } catch { }
        if (!_abnormalTermination)
        {
            _store.EndSession();
            StartupHealthService.MarkCleanExit();
        }
        _disposed = true;
        _shellRecoveryScheduler?.Dispose();
        _shellRecoveryScheduler = null;
        _displayRestoreTimer?.Stop();
        _nativeIconDisplayRepairTimer?.Stop();
        _nativeIconDisplayRepairTimer = null;
        _managedPolicyWatcher?.Dispose();
        _managedPolicyWatcher = null;
        _quickHideStartupCoordinator?.Dispose();
        _quickHideStartupCoordinator = null;
        _displayTransition?.Dispose();
        _displayTransition = null;
        _persistTimer?.Stop();
        _desktopReconcileScheduler?.Dispose();
        _dailyBackupTimer?.Stop();
        _desktopDisplay.Dispose();
        _desktopDoubleClick?.Dispose();
        _pageGesture?.Dispose();
        _looseDesktopHost?.Dispose();
        _touchPageHost?.Dispose();
        _folderPortalDropHost?.Dispose();
        _desktopCreationPreviewHost?.Dispose();
        _desktopCreationMenu?.Dispose();
        _foregroundEvents?.Dispose();
        _peekHotCorner?.Dispose();
        _peekTaskbarButton?.Dispose();
        _desktopWatchers.Dispose();
        _trayController?.Dispose();
        _trayController = null;
        if (!_safeMode && _desktopRuntimeStarted)
        {
            try { _host.SetNativeDesktopIconsVisible(true); } catch { }
        }
    }
}
