using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using CyanDeck.Interop;
using CyanDeck.Animations;
using CyanDeck.Appearance;
using CyanDeck.Models;
using CyanDeck.Layout;
using CyanDeck.ShellIntegration;
using CyanDeck.Services;
using Forms = System.Windows.Forms;

namespace CyanDeck.Views;

public partial class FenceWindow : Window
{
    private const string TabDragFormat = "CyanDeck.Tab";
    public static bool IsAnyContextMenuOpen => FenceContextMenuTheme.IsAnyMenuOpen;
    private readonly DeckSettings _settings;
    private readonly IconService _icons;
    private readonly IReadOnlyList<DeckGroupTemplate> _templates;
    private readonly Func<string, bool>? _managedCapabilityAllowed;
    private readonly Func<DeckFence, DeckTab, ItemSurfaceLayoutKind, IconSpacing>? _resolveEffectiveSpacing;
    private readonly Func<FenceWindow, double, double, Point>? _resolveMovePosition;
    private readonly FolderPortalWatcherLifetime _portalWatch = new();
    private bool _closingFromController;
    private bool _peekMode;
    private Point _dragStart;
    private ListBoxItem? _pendingSelectionCollapseItem;
    private bool _listDragStarted;
    private bool _moving;
    private bool _movePending;
    // True once the pointer-entered presentation has been applied for the current hover.
    private bool _hoverPresentationActive;
    private Point _moveStartScreen;
    private double _moveStartLeft;
    private double _moveStartTop;
    private bool _tabDragInProgress;
    private Guid? _tabPressTabId;
    private Point _tabPressStart;
    private long _tabPressStartedAt;
    private const int TabLongPressMilliseconds = 260;
    private bool _applyingLayout;
    private bool _monitorContentSwapApplying;
    private DispatcherTimer? _rollupHoverTimer;
    private bool _rollupPreviewOpen;
    private Border? _freeSelectedBorder;
    private ItemTile? _freeSelectedTile;
    private readonly HashSet<Guid> _freeSelectedIds = [];
    private readonly Dictionary<Guid, Border> _freeBorders = [];
    private readonly Dictionary<Image, WpfShortcutBadgeBridge> _shortcutBadgeBridges = [];
    private Border? _freeDragBorder;
    private DeckItem? _freeDragItem;
    private Point _freeDragStart;
    private double _freeDragStartX;
    private double _freeDragStartY;
    private bool _freeDragging;
    private DateTime _lastCutClipboardCheckUtc;
    private Rectangle? _freeSelectionRect;
    private bool _freeMarqueeActive;
    private Point _freeMarqueeStart;
    private readonly HashSet<Guid> _freeMarqueeBaseSelection = [];
    private readonly Dictionary<Guid, Stack<string>> _portalBackHistory = [];
    private readonly Dictionary<Guid, Stack<string>> _portalForwardHistory = [];
    private int _portalLoadGeneration;
    private Guid? _portalCacheTabId;
    private string? _portalCachePath;
    private string? _portalCacheVisualKey;
    private List<ItemTile> _portalTilesCache = [];
    private bool _portalCacheDirty = true;
    private Guid? _portalLoadingTabId;
    private string? _portalLoadingPath;
    private string? _portalLoadingVisualKey;
    private CancellationTokenSource? _portalLoadCts;
    private bool _itemsInitialized;
    private int _collectionLoadGeneration;
    private Guid? _collectionLoadingTabId;
    private string? _collectionLoadingVisualKey;
    private int _collectionLoadingItemCount;
    private int _collectionLoadingItemSignature;
    private int? _lastCollectionRenderSignature;
    private int? _lastPortalRenderSignature;
    private int? _lastFreeRenderSignature;
    private HwndSource? _hwndSource;
    private readonly WallpaperBlurBitmapCache _wallpaperBlurCache;
    private readonly WallpaperBlurController _wallpaperBlurController;
    // Coalesce geometry changes so a moved desktop group never paints a previous-location wallpaper slice.
    private readonly DispatcherTimer _wallpaperBlurRefreshTimer;
    private WpfChameleonActivationBridge? _chameleonActivationBridge;
    private double? _lastAppliedChameleonOpacity;
    private bool _peekPositionSaved;
    private double _prePeekLeft;
    private double _prePeekTop;
    private bool _titlebarAtBottom;
    private static AppearanceSnapshot? _appearanceClipboard;
    private bool _touchFlickTargetPreview;
    private readonly List<WpfInactiveTabTitlebarControlBridge> _inactiveTabControlBridges = [];
    private readonly WpfIconMovementAnimator _iconMovementAnimator;
    private readonly IconMovementAnimationCoordinator _iconMovementAnimationCoordinator;
    private long? _resizeIconMovementToken;
    private Guid? _resizeIconMovementTabId;

    private readonly record struct TabHeaderControlTarget(Guid FenceId, Guid TabId);

    public DeckFence Model { get; }
    public event Action? ModelChanged;
    public event Action<FenceWindow>? DeleteRequested;
    public event Action? NewFenceRequested;
    public event Action? SettingsRequested;
    public event Action? AutomationRulesChanged;
    public event Action<string>? SourcePathDropped;
    public event Action<FenceWindow, Guid, IReadOnlyList<Guid>, Point>? ItemsDetachToDesktopRequested;
    public event Action<FenceWindow>? MoveCompleted;
    public event Action<Guid, Guid, FenceWindow, int>? TabTransferRequested;
    public event Action<FenceWindow, DeckTab, Point>? TabDetachRequested;
    public event Action<FenceWindow>? DuplicateRequested;
    public event Action<FenceWindow>? SaveTemplateRequested;
    public event Action<Guid>? CreateFromTemplateRequested;
    public event Action? VisibilitySettingsChanged;
    public event Action<int>? DisplayTransitionMessage;

    internal FenceWindow(DeckFence model, DeckSettings settings, IconService icons, IReadOnlyList<DeckGroupTemplate> templates, WindowsForegroundEventSource? foregroundEvents = null, bool deferInitialItems = false, Func<string, bool>? managedCapabilityAllowed = null, Func<DeckFence, DeckTab, ItemSurfaceLayoutKind, IconSpacing>? resolveEffectiveSpacing = null, Func<FenceWindow, double, double, Point>? resolveMovePosition = null)
    {
        Model = model;
        _settings = settings;
        _icons = icons;
        _templates = templates;
        _managedCapabilityAllowed = managedCapabilityAllowed;
        _resolveEffectiveSpacing = resolveEffectiveSpacing;
        _resolveMovePosition = resolveMovePosition;
        InitializeComponent();
        // Refresh periodically *while* dragging/resizing, and once more after the final change.
        // Recreating the COM wallpaper snapshot on every mouse move would be unnecessarily costly.
        _wallpaperBlurRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _wallpaperBlurRefreshTimer.Tick += (_, _) =>
        {
            _wallpaperBlurRefreshTimer.Stop();
            RefreshWallpaperBlur();
        };
        DeckCutClipboardVisualState.Changed += RefreshCutClipboardVisuals;
        Activated += (_, _) => VerifyCutClipboardAfterInteraction();

        _iconMovementAnimator = new WpfIconMovementAnimator(ResolveIconMovementTarget);
        _iconMovementAnimationCoordinator = new IconMovementAnimationCoordinator(_iconMovementAnimator.Apply);

        _wallpaperBlurCache = new WallpaperBlurBitmapCache();
        _wallpaperBlurController = new WallpaperBlurController(
            new DesktopWallpaperSnapshotReader(),
            new WallpaperBackdropSlicePlanner(),
            _wallpaperBlurCache,
            new WallpaperBlurBackdropPresenter(WallpaperBlurBackdrop, _wallpaperBlurCache));

        if (foregroundEvents is not null)
        {
            _chameleonActivationBridge = new WpfChameleonActivationBridge(
                this,
                foregroundEvents,
                IsDesktopOrCyanDeckForeground,
                () => new ChameleonActivationContext(
                    _settings.EnableChameleon,
                    Model.AutoFade,
                    DesktopPointerHover.IsActuallyOver(this),
                    false,
                    IsVisible,
                    _peekMode),
                () => new ChameleonActivationOptions(
                    _settings.ChameleonFullColorOnMouseover,
                    _settings.ChameleonFullColorWhenDesktopInUse),
                ApplyChameleonDecision);
        }

        Left = Model.X;
        Top = Model.Y;
        Width = Math.Max(MinWidth, Model.Width);
        Height = Model.RolledUp ? 42 : Math.Max(120, Model.Height);

        LocationChanged += (_, _) =>
        {
            // Painting and persistence are independent: a transient move still needs the right
            // backdrop even when the durable group layout must not be saved yet.
            QueueWallpaperBlurRefresh();
            if (!IsLoaded || _applyingLayout || _moving || _movePending) return;
            UpdateTitlebarPlacement();
            Model.X = Left;
            Model.Y = Top;
            ModelChanged?.Invoke();
        };
        SizeChanged += (_, _) =>
        {
            QueueWallpaperBlurRefresh();
            if (!IsLoaded || _applyingLayout || Model.RolledUp) return;
            Model.Width = ActualWidth;
            Model.Height = ActualHeight;
            Model.ExpandedHeight = ActualHeight;
            ModelChanged?.Invoke();
        };
        Closing += (_, e) =>
        {
            if (_closingFromController) return;
            e.Cancel = true;
            Hide();
        };
        // Do not repair desktop Z-order from Activated. Moving this layered WPF HWND while it is
        // being activated can cause an activation/Z-order feedback loop and visible flashing.
        // Ordinary-app foreground transitions are coalesced centrally by AppController instead.
        PreviewMouseLeftButtonDown += Fence_PreviewMouseLeftButtonDown;
        PreviewMouseMove += Fence_PreviewMouseMove;
        PreviewMouseMove += Fence_HoverMouseMove;
        PreviewMouseLeftButtonUp += Fence_PreviewMouseLeftButtonUp;
        LostMouseCapture += Fence_LostMouseCapture;
        FreeCanvas.MouseMove += FreeCanvas_MouseMove;
        FreeCanvas.MouseLeftButtonUp += FreeCanvas_MouseLeftButtonUp;
        HeaderPanel.MouseRightButtonUp += (_, e) =>
        {
            var menu = BuildMenu();
            menu.PlacementTarget = HeaderPanel;
            menu.IsOpen = true;
            e.Handled = true;
        };
        Loaded += (_, _) =>
        {
            EnsureVisibleOnMonitor();
            UpdateTitlebarPlacement();
            QueueWallpaperBlurRefresh();
            // The desktop host attaches this HWND during Loaded. Reconcile after that
            // transition so every restored fence starts in its actual pointer state.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(ReconcileInitialHoverPresentation));
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // FenceWindow is a transparent/layered WPF surface. DWM's normal window transition
            // effects can briefly recompose the entire surface when its activation/Z-order context
            // changes. The Fence already owns its own movement/page animations, so disable only
            // the native DWM transition effects for this HWND. Failure is non-fatal on platforms
            // that ignore this attribute.
            try
            {
                var disableTransitions = 1;
                _ = NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
                    ref disableTransitions,
                    sizeof(int));
            }
            catch { }

            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WindowMessageHook);
        };
        Closed += (_, _) =>
        {
            _wallpaperBlurRefreshTimer.Stop();
            DeckCutClipboardVisualState.Changed -= RefreshCutClipboardVisuals;
            _portalWatch.Dispose();
            if (_hwndSource is not null)
            {
                _hwndSource.RemoveHook(WindowMessageHook);
                _hwndSource = null;
            }
            DisposeInactiveTabControlBridges();
            CancelIconMovementAnimationState(resetTransforms: true);
            _chameleonActivationBridge?.Dispose();
            _chameleonActivationBridge = null;
        };

        RefreshAppearance();
        UpdateHeaderControls(DesktopPointerHover.IsActuallyOver(this));
        RebuildTabs();
        ApplyRolledState(false);
        if (!deferInitialItems) RefreshItems();
        else
        {
            ItemsList.ItemsSource = null;
            FreeCanvas.Children.Clear();
            EmptyText.Visibility = Visibility.Collapsed;
        }
    }

    public void EnsureItemsRendered()
    {
        if (!_itemsInitialized) RefreshItems();
    }

    public void EnsureRuntimeVisible()
    {
        // Fence windows are desktop-owned surfaces and do not expose a user minimize action.
        // Shell Show Desktop / Explorer recovery can nevertheless leave the WPF top-level window
        // minimized or hidden. Normalize that runtime state before presenting the current page.
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
    }

    public void CloseFromController()
    {
        _closingFromController = true;
        foreach (var bridge in _shortcutBadgeBridges.Values.ToArray()) bridge.Dispose();
        _shortcutBadgeBridges.Clear();
        _portalLoadGeneration++;
        _collectionLoadGeneration++;
        CancelPortalLoad();
        _portalWatch.Dispose();
        _rollupHoverTimer?.Stop();
        DisposeInactiveTabControlBridges();
        CancelIconMovementAnimationState(resetTransforms: true);
        Close();
    }

    public void SetPeekMode(bool enabled)
    {
        _peekMode = enabled;
        Shell.Opacity = 1;
        _lastAppliedChameleonOpacity = 1;
        Shadow.BlurRadius = enabled ? 26 : 18;
        Shadow.Opacity = enabled ? 0.55 : 0.34;
        RefreshAppearance();
        UpdateHeaderControls(DesktopPointerHover.IsActuallyOver(this));
    }

    public void PrepareForPeek(bool respectWorkingArea)
    {
        if (!IsLoaded) return;
        if (!_peekPositionSaved)
        {
            _prePeekLeft = Left;
            _prePeekTop = Top;
            _peekPositionSaved = true;
        }
        if (!respectWorkingArea || !TryGetWorkingArea(out var work)) return;

        _applyingLayout = true;
        try
        {
            var width = Math.Max(1, double.IsNaN(Width) ? ActualWidth : Width);
            var height = Math.Max(1, double.IsNaN(Height) ? ActualHeight : Height);
            (Left, Top) = MonitorWorkAreaResolver.ClampPosition(Left, Top, width, height, work);
        }
        finally { _applyingLayout = false; }
        RefreshWallpaperBlur();
    }

    public void RestoreAfterPeek()
    {
        if (!_peekPositionSaved) return;
        _applyingLayout = true;
        try
        {
            Left = _prePeekLeft;
            Top = _prePeekTop;
        }
        finally
        {
            _peekPositionSaved = false;
            _applyingLayout = false;
        }
        RefreshWallpaperBlur();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED && lParam != IntPtr.Zero && !_monitorContentSwapApplying)
        {
            try
            {
                // Windows supplies a recommended physical-pixel rectangle for the new DPI.
                // Apply it immediately so WPF does not momentarily retain dimensions from
                // the previous monitor while its per-monitor scale catches up.
                var suggested = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
                var width = Math.Max(1, suggested.Right - suggested.Left);
                var height = Math.Max(1, suggested.Bottom - suggested.Top);
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, suggested.Left, suggested.Top, width, height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Failed to apply WM_DPICHANGED suggested bounds", ex);
            }
        }

        if (msg == NativeMethods.WM_SETTINGCHANGE)
        {
            // Windows wallpaper/theme changes can replace the bitmap without moving a group.
            // Invalidate only this group's bitmap cache, then repaint after WPF has settled.
            _wallpaperBlurController.InvalidateWallpaper();
            QueueWallpaperBlurRefresh();
        }

        if (msg is NativeMethods.WM_DPICHANGED or NativeMethods.WM_DISPLAYCHANGE)
        {
            QueueWallpaperBlurRefresh();
            if (msg == NativeMethods.WM_DISPLAYCHANGE) _wallpaperBlurController.InvalidateWallpaper();
            if (DisplayTransitionMessage is { } handler)
            {
                handler(msg);
            }
            else
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshAfterStableDisplayTransition));
            }
        }
        return IntPtr.Zero;
    }

    internal void RefreshAfterStableDisplayTransition()
    {
        if (!IsLoaded) return;
        if (_peekMode) PrepareForPeek(_settings.PeekRespectWorkingArea);
        else EnsureVisibleOnMonitor();
        if (!_peekMode)
        {
            Model.X = Left;
            Model.Y = Top;
            Model.Width = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width);
            if (!Model.RolledUp)
            {
                Model.Height = Math.Max(140, ActualHeight > 0 ? ActualHeight : Height);
                Model.ExpandedHeight = Model.Height;
            }
        }
        _wallpaperBlurController.InvalidateWallpaper();
        RefreshAppearance();
        RefreshItems(IconMovementAnimationContext.DisplayRecovery);
    }

    internal void CancelTransientInteractionForDisplayTransition()
    {
        CancelIconMovementAnimationState(resetTransforms: true);
        _moving = false;
        if (HeaderPanel.IsMouseCaptured) HeaderPanel.ReleaseMouseCapture();
        _freeDragging = false;
        if (_freeDragBorder?.IsMouseCaptured == true) _freeDragBorder.ReleaseMouseCapture();
        if (FreeCanvas.IsMouseCaptured) FreeCanvas.ReleaseMouseCapture();
        if (ItemsList.IsMouseCaptured) ItemsList.ReleaseMouseCapture();
        _freeMarqueeActive = false;
        if (_freeSelectionRect is not null)
        {
            FreeCanvas.Children.Remove(_freeSelectionRect);
            _freeSelectionRect = null;
        }
    }

    internal bool TryCaptureMonitorSwapPlacement(out ScreenContentPlacement placement)
    {
        placement = null!;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;

            var screenRect = new System.Drawing.Rectangle(rect.Left, rect.Top, width, height);
            var screen = Forms.Screen.FromRectangle(screenRect);
            placement = new ScreenContentPlacement(
                Model.Id.ToString("D"),
                screen.DeviceName,
                ScreenContentKind.Group,
                new PhysicalRect(rect.Left, rect.Top, width, height),
                Model.Locked);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void ApplyMonitorContentSwapMove(ScreenContentSwapMove move)
    {
        if (!string.Equals(move.ItemId, Model.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Swap move does not target this group.");

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("The group window is not available.");

        var target = move.AfterPx;
        var x = (int)Math.Round(target.X);
        var y = (int)Math.Round(target.Y);
        var width = Math.Max(1, (int)Math.Round(target.Width));
        var height = Math.Max(1, (int)Math.Round(target.Height));
        var parent = NativeMethods.GetParent(hwnd);
        if (parent != IntPtr.Zero && NativeMethods.GetWindowRect(parent, out var parentRect))
        {
            x -= parentRect.Left;
            y -= parentRect.Top;
        }

        var heightRatio = move.BeforePx.Height > 0
            ? Math.Clamp(move.AfterPx.Height / move.BeforePx.Height, 0.1, 10.0)
            : 1.0;

        _applyingLayout = true;
        _monitorContentSwapApplying = true;
        try
        {
            if (!NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOP,
                    x,
                    y,
                    width,
                    height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER))
            {
                throw new InvalidOperationException("Windows could not reposition the group.");
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var actual))
                throw new InvalidOperationException("Windows could not read the repositioned group bounds.");

            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            var scale = dpi is >= 48 and <= 960 ? dpi / 96d : Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);
            Left = actual.Left / scale;
            Top = actual.Top / scale;
            Width = Math.Max(MinWidth, (actual.Right - actual.Left) / scale);

            Model.X = Left;
            Model.Y = Top;
            Model.Width = Width;

            if (Model.RolledUp)
            {
                Model.Height = Math.Max(140, Model.Height * heightRatio);
                Model.ExpandedHeight = Math.Max(140, Model.ExpandedHeight * heightRatio);
                Height = 42;
            }
            else
            {
                Height = Math.Max(140, (actual.Bottom - actual.Top) / scale);
                Model.Height = Height;
                Model.ExpandedHeight = Height;
            }

            UpdateTitlebarPlacement();
        }
        finally
        {
            _monitorContentSwapApplying = false;
            _applyingLayout = false;
        }

        RefreshWallpaperBlur();
    }

    public void RefreshFromModel()
    {
        RebuildTabs();
        RefreshAppearance();
        UpdateHeaderControls(DesktopPointerHover.IsActuallyOver(this));
        ApplyModelLayout();
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        RefreshShortcutBadges();
        ApplyItemScrollbars(DesktopPointerHover.IsActuallyOver(this));
    }

    public void ApplyModelLayout()
    {
        _applyingLayout = true;
        try
        {
            Left = Model.X;
            Top = Model.Y;
            Width = Math.Max(MinWidth, Model.Width);
            if (!Model.RolledUp) Height = Math.Max(140, Model.Height);
            ApplyRolledState(false);
            EnsureVisibleOnMonitor();
        }
        finally { _applyingLayout = false; }
        RefreshWallpaperBlur();
    }

    public void EnsureVisibleOnMonitor()
    {
        if (!IsLoaded || !TryGetWorkingArea(out var work)) return;
        try
        {
            var workWidth = Math.Max(MinWidth, work.Width);
            var workHeight = Math.Max(140, work.Height);

            _applyingLayout = true;
            try
            {
                if (Width > workWidth) Width = workWidth;
                if (!Model.RolledUp && Height > workHeight) Height = workHeight;
                var width = Math.Max(1, ActualWidth > 0 ? ActualWidth : Width);
                var height = Math.Max(1, ActualHeight > 0 ? ActualHeight : Height);
                (Left, Top) = MonitorWorkAreaResolver.ClampPosition(Left, Top, width, height, work);
                Model.X = Left;
                Model.Y = Top;
                Model.Width = Width;
                if (!Model.RolledUp)
                {
                    Model.Height = Height;
                    Model.ExpandedHeight = Height;
                }
            }
            finally { _applyingLayout = false; }
            UpdateTitlebarPlacement();
        }
        catch { }
    }

    private bool TryGetWorkingArea(out Rect work)
        => MonitorWorkAreaResolver.TryGetWorkingArea(this, out work);

    public void RefreshItems()
        => RefreshItems(_itemsInitialized ? IconMovementAnimationContext.ExternalRefresh : IconMovementAnimationContext.InitialMaterialization);

    private void RefreshItems(IconMovementAnimationContext movementContext, IReadOnlyCollection<string>? excludedItemIds = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => RefreshItems(movementContext, excludedItemIds));
            return;
        }

        if (!_itemsInitialized) movementContext = IconMovementAnimationContext.InitialMaterialization;
        _itemsInitialized = true;
        var tab = SelectedTab;
        if (tab is null)
        {
            _portalLoadGeneration++;
            _collectionLoadGeneration++;
            ItemsList.ItemsSource = null;
            FreeCanvas.Children.Clear();
            DetailsHeaderPanel.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            UpdateSearchCount(0, 0);
            return;
        }

        var useFree = tab.Kind == DeckTabKind.Collection && Model.IconLayoutMode == DeckIconLayoutMode.Free && tab.ViewMode == DeckItemViewMode.Grid;
        var detailsView = !useFree && tab.ViewMode == DeckItemViewMode.Details;
        ItemsList.Visibility = useFree ? Visibility.Collapsed : Visibility.Visible;
        FreeScroll.Visibility = useFree ? Visibility.Visible : Visibility.Collapsed;
        DetailsHeaderPanel.Visibility = detailsView && tab.ShowDetailsHeader ? Visibility.Visible : Visibility.Collapsed;

        if (!useFree)
        {
            var listView = tab.ViewMode == DeckItemViewMode.List;
            var horizontalList = listView && tab.ListOrientation == DeckListOrientation.Horizontal;
            var panelKey = detailsView
                ? "ListItemsPanel"
                : listView
                    ? (horizontalList ? "HorizontalListItemsPanel" : "ListItemsPanel")
                    : "GridItemsPanel";
            ItemsList.ItemsPanel = (ItemsPanelTemplate)FindResource(panelKey);
            ItemsList.ItemTemplate = (DataTemplate)FindResource(detailsView ? "DetailsItemTemplate" : listView ? "ListItemTemplate" : "GridItemTemplate");
            ItemsList.HorizontalContentAlignment = (listView && !horizontalList) || detailsView ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            ApplyItemScrollbars(DesktopPointerHover.IsActuallyOver(this));
        }
        UpdatePortalNavigation(tab);
        ApplyDetailsColumnLayout(tab);
        UpdateDetailsHeader(tab);

        if (useFree)
        {
            _portalLoadGeneration++;
            _collectionLoadGeneration++;
            _portalWatch.Clear();
            var shown = RebuildFreeCanvas(tab, movementContext, excludedItemIds);
            EmptyText.Text = !string.IsNullOrWhiteSpace(SearchBox.Text) && shown == 0 ? "일치하는 항목 없음" : "파일을 여기에 놓으세요";
            EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (tab.Kind == DeckTabKind.FolderPortal)
        {
            _collectionLoadGeneration++;
            EnsurePortalWatcher(tab);
            RefreshPortalItemsAsync(tab, movementContext, excludedItemIds);
            return;
        }

        _portalLoadGeneration++;
        CancelPortalLoad();
        _portalWatch.Clear();
        if (_settings.ShowFilePreviews || (_settings.EnableAsyncItemLoading && tab.Items.Count >= Math.Clamp(_settings.AsyncItemLoadThreshold, 20, 1000)))
        {
            RefreshCollectionItemsAsync(tab, movementContext, excludedItemIds);
            return;
        }

        _collectionLoadGeneration++;
        var allTiles = tab.Items.Select(item => BuildTile(item)).ToList();
        ApplyCollectionTiles(tab, allTiles, movementContext, excludedItemIds);
    }

    private string CollectionVisualKey()
    {
        var spacing = GetEffectiveBaseSpacing(SelectedTab);
        return $"{Model.IconSize:F1}|{spacing.HorizontalDip:F1}|{spacing.VerticalDip:F1}|{Model.IconTint}|{Model.IconTintStrength:F3}|{Model.IconLabelMode}|linked={_settings.UseSameSpacingInsideAndOutsideGroups}";
    }

    private async void RefreshCollectionItemsAsync(DeckTab tab, IconMovementAnimationContext movementContext, IReadOnlyCollection<string>? excludedItemIds)
    {
        var items = tab.Items.ToArray();
        var visualKey = CollectionVisualKey();
        var itemSignature = items.Aggregate(17, (hash, item) => HashCode.Combine(hash, item.Id, item.Path));
        if (_collectionLoadingTabId == tab.Id
            && _collectionLoadingItemCount == items.Length
            && _collectionLoadingItemSignature == itemSignature
            && string.Equals(_collectionLoadingVisualKey, visualKey, StringComparison.Ordinal)) return;

        var generation = ++_collectionLoadGeneration;
        _collectionLoadingTabId = tab.Id;
        _collectionLoadingVisualKey = visualKey;
        _collectionLoadingItemCount = items.Length;
        _collectionLoadingItemSignature = itemSignature;
        EmptyText.Text = "항목 불러오는 중…";
        EmptyText.Visibility = Visibility.Visible;

        try
        {
            var tiles = await Task.Run(() => items.Select(item => BuildTile(item)).ToList());
            if (generation != _collectionLoadGeneration || SelectedTab?.Id != tab.Id) return;
            _collectionLoadingTabId = null;
            _collectionLoadingVisualKey = null;
            _collectionLoadingItemCount = 0;
            _collectionLoadingItemSignature = 0;
            ApplyCollectionTiles(tab, tiles, movementContext, excludedItemIds);
        }
        catch (Exception ex)
        {
            if (generation != _collectionLoadGeneration) return;
            _collectionLoadingTabId = null;
            _collectionLoadingVisualKey = null;
            _collectionLoadingItemCount = 0;
            _collectionLoadingItemSignature = 0;
            DiagnosticsLog.Error($"Collection item load failed for tab {tab.Title}", ex);
            ItemsList.ItemsSource = null;
            UpdateSearchCount(0, items.Length);
            EmptyText.Text = "항목을 불러오지 못했습니다";
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private void ApplyCollectionTiles(DeckTab tab, IReadOnlyList<ItemTile> tiles, IconMovementAnimationContext movementContext, IReadOnlyCollection<string>? excludedItemIds)
    {
        var sorted = SortTiles(tiles, tab).Where(MatchesSearch).ToList();
        var movementBatch = BeginIconMovementBatch(tab, movementContext, excludedItemIds);
        ItemsList.ItemsSource = sorted;
        ApplyItemScrollbars(DesktopPointerHover.IsActuallyOver(this));
        AnimateItemSurfaceIfChanged(ItemsList, ref _lastCollectionRenderSignature, ComputeTileSignature(sorted));
        UpdateSearchCount(sorted.Count, tiles.Count);
        EmptyText.Text = !string.IsNullOrWhiteSpace(SearchBox.Text) && sorted.Count == 0 ? "일치하는 항목 없음" : "파일을 여기에 놓으세요";
        EmptyText.Visibility = sorted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CompleteIconMovementBatchAfterLayout(movementBatch);
    }

    private string PortalVisualKey()
    {
        var spacing = GetEffectiveBaseSpacing(SelectedTab);
        return $"{Model.IconSize:F1}|{spacing.HorizontalDip:F1}|{spacing.VerticalDip:F1}|{Model.IconTint}|{Model.IconTintStrength:F3}|{Model.IconLabelMode}|linked={_settings.UseSameSpacingInsideAndOutsideGroups}";
    }

    private async void RefreshPortalItemsAsync(DeckTab tab, IconMovementAnimationContext movementContext, IReadOnlyCollection<string>? excludedItemIds)
    {
        var path = tab.PortalPath;
        // The folder-existence probe runs inside the background read below, so a slow
        // NAS/USB path never stalls the UI thread on every change notification.
        if (string.IsNullOrWhiteSpace(path))
        {
            CancelPortalLoad();
            _portalLoadGeneration++;
            _portalTilesCache = [];
            _portalCacheDirty = true;
            _portalLoadingTabId = null;
            _portalLoadingPath = null;
            _portalLoadingVisualKey = null;
            ItemsList.ItemsSource = null;
            UpdateSearchCount(0, 0);
            EmptyText.Text = "폴더를 선택하세요";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { fullPath = path; }
        var visualKey = PortalVisualKey();

        if (!_portalCacheDirty
            && _portalCacheTabId == tab.Id
            && string.Equals(_portalCachePath, fullPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_portalCacheVisualKey, visualKey, StringComparison.Ordinal))
        {
            ApplyPortalTiles(tab, _portalTilesCache, movementContext, excludedItemIds);
            return;
        }

        if (_portalLoadingTabId == tab.Id
            && string.Equals(_portalLoadingPath, fullPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_portalLoadingVisualKey, visualKey, StringComparison.Ordinal))
        {
            return;
        }

        CancelPortalLoad();
        var loadCts = new CancellationTokenSource();
        _portalLoadCts = loadCts;
        var token = loadCts.Token;
        var generation = ++_portalLoadGeneration;
        _portalLoadingTabId = tab.Id;
        _portalLoadingPath = fullPath;
        _portalLoadingVisualKey = visualKey;
        _portalCacheDirty = false;
        if (ItemsList.ItemsSource is null)
        {
            EmptyText.Text = "폴더 불러오는 중…";
            EmptyText.Visibility = Visibility.Visible;
        }

        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(fullPath))
                        return (Tiles: new List<ItemTile>(), Error: (Exception?)null, Missing: true);
                    var tiles = new List<ItemTile>();
                    foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
                    {
                        token.ThrowIfCancellationRequested();
                        tiles.Add(BuildTile(new DeckItem { Path = entry }, isFolderPortalItem: true));
                    }
                    return (Tiles: tiles, Error: (Exception?)null, Missing: false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return (Tiles: new List<ItemTile>(), Error: (Exception?)ex, Missing: false);
                }
            }, token);

            if (generation != _portalLoadGeneration || SelectedTab?.Id != tab.Id)
            {
                CompletePortalLoad(loadCts);
                return;
            }
            CompletePortalLoad(loadCts);
            _portalLoadingTabId = null;
            _portalLoadingPath = null;
            _portalLoadingVisualKey = null;

            if (result.Missing)
            {
                // Same presentation as the former synchronous missing-folder check.
                _portalTilesCache = [];
                _portalCacheDirty = true;
                ItemsList.ItemsSource = null;
                UpdateSearchCount(0, 0);
                EmptyText.Text = "폴더를 선택하세요";
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            if (result.Error is not null)
            {
                _portalCacheDirty = true;
                DiagnosticsLog.Error($"Folder Portal load failed: {fullPath}", result.Error);
                ItemsList.ItemsSource = null;
                UpdateSearchCount(0, 0);
                EmptyText.Text = "폴더를 사용할 수 없습니다";
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            var changedWhileLoading = _portalCacheDirty;
            _portalCacheTabId = tab.Id;
            _portalCachePath = fullPath;
            _portalCacheVisualKey = visualKey;
            _portalTilesCache = result.Tiles;
            _portalCacheDirty = false;
            ApplyPortalTiles(tab, result.Tiles, movementContext, excludedItemIds);
            if (changedWhileLoading)
            {
                _portalCacheDirty = true;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RefreshItems(IconMovementAnimationContext.ExternalRefresh)));
            }
        }
        catch (OperationCanceledException)
        {
            CompletePortalLoad(loadCts);
            return;
        }
        catch (Exception ex)
        {
            CompletePortalLoad(loadCts);
            if (generation != _portalLoadGeneration) return;
            _portalLoadingTabId = null;
            _portalLoadingPath = null;
            _portalLoadingVisualKey = null;
            _portalCacheDirty = true;
            DiagnosticsLog.Error($"Folder Portal refresh failed: {fullPath}", ex);
            ItemsList.ItemsSource = null;
            UpdateSearchCount(0, 0);
            EmptyText.Text = "폴더를 사용할 수 없습니다";
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private void ApplyPortalTiles(DeckTab tab, IReadOnlyList<ItemTile> tiles, IconMovementAnimationContext movementContext, IReadOnlyCollection<string>? excludedItemIds)
    {
        var sorted = SortTiles(tiles, tab).Where(MatchesSearch).ToList();
        var movementBatch = BeginIconMovementBatch(tab, movementContext, excludedItemIds);
        SetPortalItemsSourcePreservingView(tab, sorted);
        ApplyItemScrollbars(DesktopPointerHover.IsActuallyOver(this));
        AnimateItemSurfaceIfChanged(ItemsList, ref _lastPortalRenderSignature, ComputeTileSignature(sorted));
        UpdateSearchCount(sorted.Count, tiles.Count);
        EmptyText.Text = !string.IsNullOrWhiteSpace(SearchBox.Text) && sorted.Count == 0 ? "일치하는 항목 없음" : "폴더가 비어 있습니다";
        EmptyText.Visibility = sorted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CompleteIconMovementBatchAfterLayout(movementBatch);
    }

    private int RebuildFreeCanvas(DeckTab tab, IconMovementAnimationContext movementContext, IReadOnlyCollection<string>? excludedItemIds)
    {
        var movementBatch = BeginIconMovementBatch(tab, movementContext, excludedItemIds);
        FreeCanvas.Children.Clear();
        _freeSelectionRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(210, 122, 162, 255)),
            Fill = new SolidColorBrush(Color.FromArgb(42, 122, 162, 255)),
            StrokeThickness = 1,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_freeSelectionRect, 999);
        FreeCanvas.Children.Add(_freeSelectionRect);
        _freeBorders.Clear();
        _freeSelectedBorder = null;
        _freeSelectedTile = null;
        var validIds = tab.Items.Select(i => i.Id).ToHashSet();
        _freeSelectedIds.RemoveWhere(id => !validIds.Contains(id));

        var allTiles = tab.Items.Select(item => BuildTile(item)).ToList();
        var tiles = allTiles.Where(MatchesSearch).ToList();
        UpdateSearchCount(tiles.Count, allTiles.Count);
        var metrics = GetGridTileMetrics();
        var cellWidth = metrics.TileWidth + 4;
        var cellHeight = metrics.TileHeight + 4;
        var usable = Math.Max(cellWidth, Math.Max(200, ActualWidth - 28));
        var columns = Math.Max(1, (int)Math.Floor(usable / cellWidth));
        var maxRight = 0d;
        var maxBottom = 0d;

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var item = tile.Source;
            if (item.PositionX is null || item.PositionY is null)
            {
                item.PositionX = 6 + (i % columns) * cellWidth;
                item.PositionY = 6 + (i / columns) * cellHeight;
            }

            var border = CreateFreeTile(tile);
            var x = Math.Max(0, item.PositionX.Value);
            var y = Math.Max(0, item.PositionY.Value);
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            FreeCanvas.Children.Add(border);
            _freeBorders[item.Id] = border;
            UpdateFreeTileSelectionVisual(border, tile);
            maxRight = Math.Max(maxRight, x + tile.TileWidth + 12);
            maxBottom = Math.Max(maxBottom, y + tile.TileHeight + 12);
        }

        FreeCanvas.Width = Math.Max(Math.Max(200, ActualWidth - 24), maxRight);
        FreeCanvas.Height = Math.Max(Math.Max(120, ActualHeight - 70), maxBottom);
        ApplyItemScrollbars(DesktopPointerHover.IsActuallyOver(this));
        AnimateItemSurfaceIfChanged(FreeCanvas, ref _lastFreeRenderSignature, ComputeTileSignature(tiles));
        CompleteIconMovementBatchAfterLayout(movementBatch);
        return tiles.Count;
    }

    private Border CreateFreeTile(ItemTile tile)
    {
        var border = new Border
        {
            Width = tile.TileWidth,
            Height = tile.TileHeight,
            Tag = tile,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Arrow
        };
        if (_settings.AnimateIconsWhenMoved) border.RenderTransform = new TranslateTransform();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(tile.IconRowHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var image = new Image
        {
            Source = tile.Icon,
            Width = tile.IconDisplaySize,
            Height = tile.IconDisplaySize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        Grid.SetRow(image, 0);
        AttachShortcutBadge(image, tile);
        var text = new TextBlock
        {
            Text = tile.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(244, 246, 250)),
            FontSize = Math.Clamp(Model.IconLabelFontSize, 9, 24),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = Math.Ceiling(Math.Clamp(Model.IconLabelFontSize, 9, 24) * 2.5 + 2),
            Margin = new Thickness(2, 2, 2, 0),
            ToolTip = tile.Path,
            IsHitTestVisible = false,
            Visibility = Model.IconLabelMode == DeckIconLabelMode.Always ? Visibility.Visible : Visibility.Collapsed
        };
        if (Model.IconLabelMode == DeckIconLabelMode.Mouseover)
        {
            border.MouseEnter += (_, _) => text.Visibility = Visibility.Visible;
            border.MouseLeave += (_, _) => text.Visibility = Visibility.Collapsed;
        }
        Grid.SetRow(text, 1);
        grid.Children.Add(image);
        grid.Children.Add(text);
        border.Child = grid;
        border.MouseLeftButtonDown += FreeTile_MouseLeftButtonDown;
        border.MouseMove += FreeTile_MouseMove;
        border.MouseLeftButtonUp += FreeTile_MouseLeftButtonUp;
        border.MouseRightButtonDown += FreeTile_MouseRightButtonDown;
        return border;
    }

    private void UpdateFreeTileSelectionVisual(Border border, ItemTile tile)
    {
        var selected = _freeSelectedIds.Contains(tile.Source.Id);
        border.Opacity = tile.CutOpacity;
        border.Background = selected
            ? new SolidColorBrush(Color.FromArgb(58, 120, 168, 255))
            : Brushes.Transparent;
    }

    private void VerifyCutClipboardAfterInteraction()
    {
        if (!DeckCutClipboardVisualState.HasCut) return;
        var now = DateTime.UtcNow;
        if ((now - _lastCutClipboardCheckUtc).TotalMilliseconds < 1200) return;
        _lastCutClipboardCheckUtc = now;
        DeckShellClipboardService.VerifyCutClipboard(Dispatcher);
    }

    private void RefreshCutClipboardVisuals()
    {
        // Only visible current-page tiles are touched. RefreshItems/builds new tiles
        // with the same shared visual state, so layout and selection remain unchanged.
        foreach (var tile in ItemsList.Items.OfType<ItemTile>()) tile.UpdateCutOpacity();
        foreach (var border in _freeBorders.Values)
        {
            if (border.Tag is not ItemTile tile) continue;
            tile.UpdateCutOpacity();
            border.Opacity = tile.CutOpacity;
        }
    }

    private void ClearFreeSelection()
    {
        _freeSelectedIds.Clear();
        _freeSelectedBorder = null;
        _freeSelectedTile = null;
        foreach (var pair in _freeBorders)
            if (pair.Value.Tag is ItemTile tile) UpdateFreeTileSelectionVisual(pair.Value, tile);
    }

    private void SelectFreeTile(Border border, ItemTile tile, bool toggle = false, bool extend = false)
    {
        if (!toggle && !extend) _freeSelectedIds.Clear();
        if (toggle)
        {
            if (!_freeSelectedIds.Add(tile.Source.Id)) _freeSelectedIds.Remove(tile.Source.Id);
        }
        else _freeSelectedIds.Add(tile.Source.Id);

        _freeSelectedBorder = _freeSelectedIds.Contains(tile.Source.Id) ? border : null;
        _freeSelectedTile = _freeSelectedIds.Contains(tile.Source.Id) ? tile : null;
        foreach (var pair in _freeBorders)
            if (pair.Value.Tag is ItemTile candidate) UpdateFreeTileSelectionVisual(pair.Value, candidate);
    }

    private void FreeCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != FreeCanvas || e.LeftButton != MouseButtonState.Pressed) return;
        var selectionModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (!selectionModifier) return;
        CancelPendingDeckMove();
        _freeMarqueeActive = true;
        _freeMarqueeStart = e.GetPosition(FreeCanvas);
        _freeMarqueeBaseSelection.Clear();
        var extend = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (extend)
            foreach (var id in _freeSelectedIds) _freeMarqueeBaseSelection.Add(id);
        else
            ClearFreeSelection();

        if (_freeSelectionRect is not null)
        {
            Canvas.SetLeft(_freeSelectionRect, _freeMarqueeStart.X);
            Canvas.SetTop(_freeSelectionRect, _freeMarqueeStart.Y);
            _freeSelectionRect.Width = 0;
            _freeSelectionRect.Height = 0;
            _freeSelectionRect.Visibility = Visibility.Visible;
        }
        FreeCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void FreeCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_freeMarqueeActive || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(FreeCanvas);
        var left = Math.Min(_freeMarqueeStart.X, now.X);
        var top = Math.Min(_freeMarqueeStart.Y, now.Y);
        var right = Math.Max(_freeMarqueeStart.X, now.X);
        var bottom = Math.Max(_freeMarqueeStart.Y, now.Y);
        var rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        if (_freeSelectionRect is not null)
        {
            Canvas.SetLeft(_freeSelectionRect, left);
            Canvas.SetTop(_freeSelectionRect, top);
            _freeSelectionRect.Width = rect.Width;
            _freeSelectionRect.Height = rect.Height;
        }

        _freeSelectedIds.Clear();
        foreach (var id in _freeMarqueeBaseSelection) _freeSelectedIds.Add(id);
        foreach (var pair in _freeBorders)
        {
            var border = pair.Value;
            var tileRect = new Rect(Canvas.GetLeft(border), Canvas.GetTop(border), border.ActualWidth > 0 ? border.ActualWidth : border.Width, border.ActualHeight > 0 ? border.ActualHeight : border.Height);
            if (rect.IntersectsWith(tileRect)) _freeSelectedIds.Add(pair.Key);
            if (border.Tag is ItemTile tile) UpdateFreeTileSelectionVisual(border, tile);
        }
        _freeSelectedTile = _freeSelectedIds.Select(id => _freeBorders.GetValueOrDefault(id)?.Tag).OfType<ItemTile>().FirstOrDefault();
        _freeSelectedBorder = _freeSelectedTile is null ? null : _freeBorders.GetValueOrDefault(_freeSelectedTile.Source.Id);
        e.Handled = true;
    }

    private void FreeCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_freeMarqueeActive) return;
        _freeMarqueeActive = false;
        FreeCanvas.ReleaseMouseCapture();
        if (_freeSelectionRect is not null) _freeSelectionRect.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void FreeTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ItemTile tile) return;
        var toggle = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var extend = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!toggle && !extend && _freeSelectedIds.Contains(tile.Source.Id) && _freeSelectedIds.Count > 1)
        {
            _freeSelectedBorder = border;
            _freeSelectedTile = tile;
        }
        else SelectFreeTile(border, tile, toggle, extend);
        if (e.ClickCount >= 2)
        {
            CancelPendingDeckMove();
            OpenItem(tile);
            e.Handled = true;
            return;
        }
        _freeDragBorder = border;
        _freeDragItem = tile.Source;
        _freeDragStart = e.GetPosition(FreeCanvas);
        _freeDragStartX = Canvas.GetLeft(border);
        _freeDragStartY = Canvas.GetTop(border);
        _freeDragging = false;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void FreeTile_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_freeDragBorder is null || _freeDragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(FreeCanvas);
        if (now.X < -24 || now.Y < -24 || now.X > FreeCanvas.ActualWidth + 24 || now.Y > FreeCanvas.ActualHeight + 24)
        {
            var selected = GetSelectedTiles().Select(t => t.Path).Where(p => File.Exists(p) || Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (selected.Length == 0 && !DesktopSystemItems.IsSupportedVirtualPath(_freeDragItem.Path))
                selected = [_freeDragItem.Path];
            if (selected.Length == 0) return; // Shell namespace items are not filesystem drag payloads.
            var source = _freeDragBorder;
            source.ReleaseMouseCapture();
            _freeDragBorder = null;
            _freeDragItem = null;
            _freeDragging = false;
            var data = new DataObject(DataFormats.FileDrop, selected);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Link);
            e.Handled = true;
            return;
        }
        var dx = now.X - _freeDragStart.X;
        var dy = now.Y - _freeDragStart.Y;
        if (!_freeDragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
        _freeDragging = true;
        var x = Math.Max(0, _freeDragStartX + dx);
        var y = Math.Max(0, _freeDragStartY + dy);
        Canvas.SetLeft(_freeDragBorder, x);
        Canvas.SetTop(_freeDragBorder, y);
        FreeCanvas.Width = Math.Max(FreeCanvas.Width, x + _freeDragBorder.ActualWidth + 18);
        FreeCanvas.Height = Math.Max(FreeCanvas.Height, y + _freeDragBorder.ActualHeight + 18);
        e.Handled = true;
    }

    private void FreeTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_freeDragBorder is null || _freeDragItem is null) return;
        if (_freeDragging)
        {
            _freeDragItem.PositionX = Canvas.GetLeft(_freeDragBorder);
            _freeDragItem.PositionY = Canvas.GetTop(_freeDragBorder);
            ModelChanged?.Invoke();
        }
        _freeDragBorder.ReleaseMouseCapture();
        _freeDragBorder = null;
        _freeDragItem = null;
        _freeDragging = false;
        e.Handled = true;
    }

    private void FreeTile_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ItemTile tile) return;
        if (!_freeSelectedIds.Contains(tile.Source.Id)) SelectFreeTile(border, tile);
        var selected = GetSelectedTiles();
        if (selected.Count > 1) ShowBatchContextMenu(selected, border, e);
        else ShowItemContextMenu(tile, border, e.GetPosition(this), e);
    }

    private ShortcutBadgeOptions GetShortcutBadgeOptions() => new()
    {
        HideFileShortcutBadges = _settings.HideFileShortcutBadges,
        HideInternetShortcutBadges = _settings.HideInternetShortcutBadges,
        IncludeFolderPortalItems = _settings.ShowShortcutBadgesInFolderPortals,
    };

    private void ItemIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image && image.DataContext is ItemTile tile) AttachShortcutBadge(image, tile);
    }

    private void ItemIcon_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || !_shortcutBadgeBridges.Remove(image, out var bridge)) return;
        bridge.Dispose();
    }

    private void AttachShortcutBadge(Image image, ItemTile tile)
    {
        if (_shortcutBadgeBridges.Remove(image, out var previous)) previous.Dispose();
        var bridge = new WpfShortcutBadgeBridge(
            image,
            () => new ShortcutBadgeContext
            {
                ItemPath = tile.Path,
                IsFolderPortalItem = tile.IsFolderPortalItem,
                IsDesktopBackedItem = !tile.IsFolderPortalItem,
                IsItemInteractive = IsVisible && !_closingFromController,
            },
            GetShortcutBadgeOptions,
            _ => { });
        _shortcutBadgeBridges[image] = bridge;
    }

    private void RefreshShortcutBadges()
    {
        foreach (var bridge in _shortcutBadgeBridges.Values.ToArray())
        {
            try { bridge.Refresh(); } catch { }
        }
    }

    private ItemTile BuildTile(DeckItem item, bool isFolderPortalItem = false)
    {
        var tint = ParseColor(Model.IconTint, Colors.White);
        var shortcutDecision = ShortcutBadgePolicy.Evaluate(new ShortcutBadgeContext
        {
            ItemPath = item.Path,
            IsFolderPortalItem = isFolderPortalItem,
            IsDesktopBackedItem = !isFolderPortalItem,
            IsItemInteractive = true,
        }, GetShortcutBadgeOptions());
        ImageSource icon;
        try { icon = _icons.Get(item.Path, Model.IconSize, tint, Model.IconTintStrength, shortcutDecision.SuppressHostShortcutOverlay); }
        catch
        {
            var fallback = new DrawingImage();
            fallback.Freeze();
            icon = fallback;
        }

        DateTime modified = DateTime.MinValue;
        DateTime createdUtc = DateTime.MinValue;
        long size = 0;
        var isDirectory = false;
        // One metadata read per tile (normally on the background loader). The folder flag
        // is stored on the tile so type/size text, "folders first", sorting and search
        // never repeat File/Directory probes on slow NAS or removable drives.
        try
        {
            if (!DesktopSystemItems.IsShellNamespacePath(item.Path))
            {
                var fi = new FileInfo(item.Path);
                if (fi.Exists)
                {
                    modified = fi.LastWriteTime;
                    createdUtc = fi.CreationTimeUtc;
                    size = fi.Length;
                }
                else
                {
                    var di = new DirectoryInfo(item.Path);
                    if (di.Exists)
                    {
                        isDirectory = true;
                        modified = di.LastWriteTime;
                        createdUtc = di.CreationTimeUtc;
                    }
                }
            }
        }
        catch { }

        var metrics = GetGridTileMetrics();
        return new ItemTile(item, icon, modified, size, metrics.IconSize, metrics.TileWidth, metrics.TileHeight, metrics.IconRowHeight, Model.IconLabelMode, isFolderPortalItem)
        {
            IsDirectory = isDirectory,
            CreatedUtc = createdUtc,
        };
    }

    private ItemSurfaceLayoutKind GetCurrentSurfaceLayoutKind(DeckTab? tab = null)
    {
        tab ??= SelectedTab;
        if (tab is null) return ItemSurfaceLayoutKind.Grid;
        if (tab.Kind == DeckTabKind.Collection && Model.IconLayoutMode == DeckIconLayoutMode.Free && tab.ViewMode == DeckItemViewMode.Grid)
            return ItemSurfaceLayoutKind.FreePlacement;
        return tab.ViewMode switch
        {
            DeckItemViewMode.List => ItemSurfaceLayoutKind.List,
            DeckItemViewMode.Details => ItemSurfaceLayoutKind.Details,
            _ => ItemSurfaceLayoutKind.Grid
        };
    }

    private IconSpacing GetEffectiveBaseSpacing(DeckTab? tab = null)
    {
        var saved = new IconSpacing(Model.IconSpacingX, Model.IconSpacingY);
        tab ??= SelectedTab;
        if (tab is null || _resolveEffectiveSpacing is null) return IconSpacing.Normalize(saved, new IconSpacing(0, 0));
        try
        {
            return IconSpacing.Normalize(_resolveEffectiveSpacing(Model, tab, GetCurrentSurfaceLayoutKind(tab)), saved);
        }
        catch
        {
            return IconSpacing.Normalize(saved, new IconSpacing(0, 0));
        }
    }

    private (double IconSize, double TileWidth, double TileHeight, double IconRowHeight) GetGridTileMetrics()
    {
        var iconSize = Math.Clamp(Model.IconSize, 24, 128);
        var spacing = GetEffectiveBaseSpacing();
        // At large/extra-large sizes, a small fixed padding is not enough once WPF
        // rounds at 150-400% DPI. Add proportional safety room after resolving the
        // saved-vs-linked base spacing. The safety layer is never persisted.
        var largeBoost = Math.Max(0, iconSize - 48);
        var safeX = Math.Max(spacing.HorizontalDip, 6 + largeBoost * 0.20);
        var safeY = Math.Max(spacing.VerticalDip, 6 + largeBoost * 0.16);
        var fontSize = Math.Clamp(Model.IconLabelFontSize, 9, 24);
        var tileWidth = Math.Max(58, iconSize + 30 + safeX + Math.Max(0, fontSize - 11.5) * 1.5);
        var iconRowHeight = iconSize + 8 + safeY * 0.35;
        var labelHeight = Model.IconLabelMode == DeckIconLabelMode.Hidden ? 8 : Math.Max(34, Math.Ceiling(fontSize * 2.5 + 2));
        var tileHeight = Math.Max(Model.IconLabelMode == DeckIconLabelMode.Hidden ? 52 : 68,
            iconRowHeight + labelHeight + safeY * 0.65);
        return (iconSize, Math.Ceiling(tileWidth), Math.Ceiling(tileHeight), Math.Ceiling(iconRowHeight));
    }

    internal void RefreshLinkedIconSpacing()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var kind = GetCurrentSurfaceLayoutKind(tab);
        if (kind == ItemSurfaceLayoutKind.Grid)
        {
            _portalCacheDirty = true;
            RefreshItems(IconMovementAnimationContext.ExternalRefresh);
        }
    }

    private static IEnumerable<ItemTile> SortTiles(IEnumerable<ItemTile> items, DeckTab tab)
    {
        Func<ItemTile, object> key = tab.Sort switch
        {
            DeckSortMode.Type => i => i.Type,
            DeckSortMode.Modified => i => i.Modified,
            // Folder Portal items are rebuilt on every refresh, so "added" means file creation time there.
            DeckSortMode.Added => i => i.IsFolderPortalItem ? i.CreatedUtc : i.Source.AddedUtc,
            DeckSortMode.Size => i => i.Size,
            DeckSortMode.OpenCount => i => i.Source.OpenCount,
            _ => i => i.Name
        };
        var source = items.ToList();
        if (tab.FoldersFirst)
        {
            return tab.SortDescending
                ? source.OrderBy(i => i.IsDirectory ? 0 : 1).ThenByDescending(key).ThenBy(i => i.Name)
                : source.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(key).ThenBy(i => i.Name);
        }
        return tab.SortDescending ? source.OrderByDescending(key).ThenBy(i => i.Name) : source.OrderBy(key).ThenBy(i => i.Name);
    }

    private void EnsurePortalWatcher(DeckTab tab)
    {
        var path = tab.PortalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _portalWatch.Clear();
            return;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { fullPath = path; }
        // Already watching this folder: skip the (possibly slow NAS/USB) existence probe
        // that every coalesced refresh would otherwise repeat on the UI thread.
        if (string.Equals(_portalWatch.CurrentPath, fullPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(fullPath))
        {
            _portalWatch.Clear();
            return;
        }
        _portalWatch.Bind(
            fullPath,
            action => _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, action),
            // Bursts of changes are coalesced into one refresh (QueuePortalWatcherRefresh).
            (changedPath, oldPath) => QueuePortalWatcherRefresh(changedPath, oldPath),
            error =>
            {
                _portalCacheDirty = true;
                DiagnosticsLog.Error($"Folder Portal watcher error: {fullPath}", error);
                RefreshItems(IconMovementAnimationContext.ExternalRefresh);
            });
    }

    private DeckTab? SelectedTab
    {
        get
        {
            if (Model.Tabs.Count == 0) return null;
            Model.SelectedTabIndex = Math.Clamp(Model.SelectedTabIndex, 0, Model.Tabs.Count - 1);
            return Model.Tabs[Model.SelectedTabIndex];
        }
    }

    private void RebuildTabs()
    {
        DisposeInactiveTabControlBridges();
        TabsPanel.Children.Clear();
        if (Model.Tabs.Count == 0) return;
        Model.SelectedTabIndex = Math.Clamp(Model.SelectedTabIndex, 0, Model.Tabs.Count - 1);

        IEnumerable<(DeckTab Tab, int Index)> tabs = Model.Tabs.Select((t, i) => (t, i));
        if (Model.TabStyle == DeckTabStyle.MenuOnly)
            tabs = tabs.Where(x => x.Index == Model.SelectedTabIndex);

        foreach (var (tab, i) in tabs)
        {
            var selected = i == Model.SelectedTabIndex;
            var accent = ParseColor(tab.Accent ?? Model.Accent, Colors.CornflowerBlue);
            var titleColor = ParseColor(Model.TitleForeground, Color.FromRgb(244, 246, 250));
            var titleBrush = new SolidColorBrush(selected ? titleColor : Color.FromArgb(190, titleColor.R, titleColor.G, titleColor.B));
            var titleText = (tab.Kind == DeckTabKind.FolderPortal && tab.ShowPortalFolderIcon ? "📁 " : "") + tab.Title +
                            (Model.TabStyle == DeckTabStyle.MenuOnly ? "  ▾" : "");
            var header = new Grid
            {
                Tag = tab,
                Height = 30,
                AllowDrop = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var button = new Button
            {
                Content = titleText,
                Tag = tab,
                MinWidth = 48,
                MaxWidth = 210,
                Height = 30,
                Foreground = titleBrush,
                FontFamily = new FontFamily(string.IsNullOrWhiteSpace(Model.TitleFontFamily) ? "Segoe UI" : Model.TitleFontFamily),
                FontSize = Math.Clamp(Model.TitleFontSize, 9, 24),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                AllowDrop = true,
                ToolTip = tab.Kind == DeckTabKind.FolderPortal
                    ? $"{tab.PortalPath}\n길게 누른 뒤 드래그: 탭 순서 변경 · Shift+드래그: 이동/분리"
                    : "길게 누른 뒤 드래그: 탭 순서 변경 · Shift+드래그: 이동/분리"
            };

            switch (Model.TabStyle)
            {
                case DeckTabStyle.Flat:
                    button.Padding = new Thickness(12, 5, 12, 5);
                    button.Margin = new Thickness(0, 0, 2, 0);
                    button.BorderThickness = selected ? new Thickness(0, 0, 0, 2) : new Thickness(0);
                    button.BorderBrush = new SolidColorBrush(accent);
                    button.Background = new SolidColorBrush(selected ? Color.FromArgb(42, 255, 255, 255) : Colors.Transparent);
                    break;
                case DeckTabStyle.Segmented:
                    button.Padding = new Thickness(11, 5, 11, 5);
                    button.Margin = new Thickness(0, 0, 2, 0);
                    button.BorderThickness = new Thickness(1);
                    button.BorderBrush = new SolidColorBrush(selected ? Color.FromArgb(180, accent.R, accent.G, accent.B) : Color.FromArgb(28, 255, 255, 255));
                    button.Background = new SolidColorBrush(selected ? Color.FromArgb(62, accent.R, accent.G, accent.B) : Color.FromArgb(12, 255, 255, 255));
                    break;
                default:
                    button.Padding = new Thickness(11, 5, 11, 5);
                    button.Margin = new Thickness(0, 0, 4, 0);
                    button.BorderThickness = selected ? new Thickness(0, 0, 0, 2) : new Thickness(0);
                    button.BorderBrush = new SolidColorBrush(accent);
                    button.Background = new SolidColorBrush(selected ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(8, 255, 255, 255));
                    break;
            }

            var target = new TabHeaderControlTarget(Model.Id, tab.Id);
            var inactiveControls = new StackPanel
            {
                Tag = target,
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Margin = new Thickness(-2, 0, 2, 0)
            };
            Grid.SetColumn(inactiveControls, 1);

            var menuButton = CreateInactiveTabHeaderControlButton("⋯", "이 탭의 메뉴", target);
            menuButton.Click += (_, e) =>
            {
                e.Handled = true;
                OpenInactiveTabMenu(target.TabId, menuButton);
            };
            inactiveControls.Children.Add(menuButton);

            var closeButton = CreateInactiveTabHeaderControlButton("×", "이 탭 제거", target);
            closeButton.IsEnabled = Model.Tabs.Count > 1 && CapabilityAllowed("Settings.EditGroups");
            closeButton.Click += (_, e) =>
            {
                e.Handled = true;
                RemoveTabById(target.TabId);
            };
            inactiveControls.Children.Add(closeButton);

            button.Click += (_, _) => SelectTabById(target.TabId);
            header.MouseEnter += (_, _) =>
            {
                if (!TabSwitchActivationPolicy.ShouldSelectOnPointerEnter(
                    _settings.SwitchTabsOnHover,
                    SelectedTab?.Id == target.TabId,
                    Mouse.LeftButton == MouseButtonState.Pressed,
                    _tabDragInProgress)) return;
                SelectTabById(target.TabId);
            };
            header.Drop += Tab_Drop;
            header.DragOver += Window_DragOver;
            header.PreviewMouseLeftButtonDown += Tab_PreviewMouseLeftButtonDown;
            header.PreviewMouseLeftButtonUp += Tab_PreviewMouseLeftButtonUp;
            header.PreviewMouseMove += Tab_PreviewMouseMove;
            Grid.SetColumn(button, 0);
            header.Children.Add(button);
            header.Children.Add(inactiveControls);
            TabsPanel.Children.Add(header);

            var bridge = new WpfInactiveTabTitlebarControlBridge(
                header,
                isSelectedTab: () => SelectedTab?.Id == target.TabId,
                isHeaderPresented: () => HeaderContent.IsVisible && header.IsVisible && Model.TabStyle != DeckTabStyle.MenuOnly,
                isGroupInteractive: () => !_closingFromController && IsVisible && IsEnabled,
                getOptions: () => new InactiveTabTitlebarControlOptions
                {
                    ShowControlsInInactiveTabs = _settings.ShowTitlebarControlsInInactiveTabs,
                    ShowControlsOnMouseover = _settings.ShowTitlebarControlsOnMouseover,
                    KeyboardFocusRevealsControls = true,
                },
                applyInactiveOverride: visible =>
                {
                    inactiveControls.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    inactiveControls.IsHitTestVisible = visible;
                },
                clearInactiveOverride: () =>
                {
                    inactiveControls.Visibility = Visibility.Collapsed;
                    inactiveControls.IsHitTestVisible = false;
                });
            _inactiveTabControlBridges.Add(bridge);
        }
    }

    private Button CreateInactiveTabHeaderControlButton(string content, string toolTip, TabHeaderControlTarget target)
    {
        var button = new Button
        {
            Content = content,
            Tag = target,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 240, 248)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            ToolTip = toolTip,
            Focusable = true
        };
        System.Windows.Automation.AutomationProperties.SetName(button, toolTip);
        return button;
    }

    private void DisposeInactiveTabControlBridges()
    {
        foreach (var bridge in _inactiveTabControlBridges)
        {
            try { bridge.Dispose(); } catch { }
        }
        _inactiveTabControlBridges.Clear();
    }

    private void RefreshInactiveTabControlBridges()
    {
        foreach (var bridge in _inactiveTabControlBridges.ToArray())
        {
            try { bridge.Refresh(); } catch { }
        }
    }

    private DeckTab? FindTabById(Guid tabId) => Model.Tabs.FirstOrDefault(t => t.Id == tabId);

    private void SelectTabById(Guid tabId)
    {
        var tab = FindTabById(tabId);
        if (tab is not null) SelectTab(tab);
    }

    private void OpenInactiveTabMenu(Guid tabId, FrameworkElement placementTarget)
    {
        var tab = FindTabById(tabId);
        if (tab is null) return;

        var menu = CreateThemedContextMenu();
        menu.PlacementTarget = placementTarget;
        menu.Items.Add(MakeItem("탭 선택", () => SelectTabById(tabId)));
        if (tab.Kind == DeckTabKind.FolderPortal && !string.IsNullOrWhiteSpace(tab.PortalPath))
            menu.Items.Add(MakeItem("폴더를 Explorer에서 열기", () =>
            {
                var current = FindTabById(tabId);
                if (!string.IsNullOrWhiteSpace(current?.PortalPath)) Open(current.PortalPath);
            }));
        menu.Items.Add(new Separator());

        var currentIndex = Model.Tabs.FindIndex(t => t.Id == tabId);
        var moveLeft = MakeItem("왼쪽으로 이동", () => MoveTabById(tabId, -1));
        moveLeft.IsEnabled = currentIndex > 0 && CapabilityAllowed("Settings.EditGroups");
        menu.Items.Add(moveLeft);
        var moveRight = MakeItem("오른쪽으로 이동", () => MoveTabById(tabId, 1));
        moveRight.IsEnabled = currentIndex >= 0 && currentIndex < Model.Tabs.Count - 1 && CapabilityAllowed("Settings.EditGroups");
        menu.Items.Add(moveRight);

        var detach = MakeItem("탭 분리", () => DetachTabById(tabId));
        detach.IsEnabled = Model.Tabs.Count > 1 && CapabilityAllowed("Layout.CreateDeleteGroups");
        menu.Items.Add(detach);
        var remove = MakeItem("탭 제거", () => RemoveTabById(tabId));
        remove.IsEnabled = Model.Tabs.Count > 1 && CapabilityAllowed("Settings.EditGroups");
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private void MoveTabById(Guid tabId, int delta)
    {
        if (!CapabilityAllowed("Settings.EditGroups")) return;
        var current = Model.Tabs.FindIndex(t => t.Id == tabId);
        if (current < 0 || Model.Tabs.Count < 2) return;
        var target = Math.Clamp(current + delta, 0, Model.Tabs.Count - 1);
        if (target == current) return;
        var selectedId = SelectedTab?.Id;
        var tab = Model.Tabs[current];
        Model.Tabs.RemoveAt(current);
        Model.Tabs.Insert(target, tab);
        if (selectedId is Guid selected)
        {
            var selectedIndex = Model.Tabs.FindIndex(t => t.Id == selected);
            Model.SelectedTabIndex = selectedIndex >= 0 ? selectedIndex : Math.Clamp(target, 0, Model.Tabs.Count - 1);
        }
        RebuildTabs();
        ModelChanged?.Invoke();
    }

    private void DetachTabById(Guid tabId)
    {
        if (!CapabilityAllowed("Layout.CreateDeleteGroups") || Model.Tabs.Count <= 1) return;
        var tab = FindTabById(tabId);
        if (tab is null) return;
        TabDetachRequested?.Invoke(this, tab, GetCursorScreenDip());
    }

    private void RemoveTabById(Guid tabId)
    {
        if (!CapabilityAllowed("Settings.EditGroups") || Model.Tabs.Count <= 1) return;
        var current = Model.Tabs.FindIndex(t => t.Id == tabId);
        if (current < 0) return;
        var selectedId = SelectedTab?.Id;
        Model.Tabs.RemoveAt(current);
        if (selectedId is Guid selected)
        {
            var selectedIndex = Model.Tabs.FindIndex(t => t.Id == selected);
            Model.SelectedTabIndex = selectedIndex >= 0 ? selectedIndex : Math.Clamp(current, 0, Model.Tabs.Count - 1);
        }
        else
        {
            Model.SelectedTabIndex = Math.Clamp(current, 0, Model.Tabs.Count - 1);
        }
        RebuildTabs();
        RefreshItems(IconMovementAnimationContext.InitialMaterialization);
        ModelChanged?.Invoke();
    }

    private void SelectTab(DeckTab tab)
    {
        var idx = Model.Tabs.IndexOf(tab);
        if (idx < 0) return;
        Model.SelectedTabIndex = idx;
        RebuildTabs();
        RefreshItems(IconMovementAnimationContext.InitialMaterialization);
        ModelChanged?.Invoke();
    }

    private bool CapabilityAllowed(string capability) => _managedCapabilityAllowed?.Invoke(capability) != false;

    public void RefreshManagedPolicyCapabilities()
    {
        RebuildTabs();
        RefreshAppearance();
        RefreshInactiveTabControlBridges();
    }

    internal bool TryGetPhysicalBounds(out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;
        bounds = new System.Drawing.Rectangle(rect.Left, rect.Top, width, height);
        return true;
    }

    internal void SetTouchFlickTargetPreview(bool active)
    {
        if (_touchFlickTargetPreview == active) return;
        _touchFlickTargetPreview = active;
        RefreshAppearance();
    }

    private void RefreshAppearance()
    {
        // Keep all template instances in this group in sync; no global application font override.
        var labelFont = Math.Clamp(Model.IconLabelFontSize, 9, 24);
        Resources["DeckGridLabelFontSize"] = labelFont;
        Resources["DeckListLabelFontSize"] = labelFont + 0.5;
        Resources["DeckSecondaryLabelFontSize"] = Math.Max(9, labelFont - 1);
        Resources["DeckGridLabelMaxHeight"] = Math.Ceiling(labelFont * 2.5 + 2);
        var bg = ParseColor(Model.Background, Color.FromRgb(24, 26, 31));
        var accent = ParseColor(Model.Accent, Color.FromRgb(122, 162, 255));
        var alpha = (byte)Math.Clamp((int)Math.Round(Model.Opacity * 255), 40, 250);
        var headerAlpha = (byte)Math.Clamp(alpha + 28, 50, 250);

        Shell.CornerRadius = new CornerRadius(Model.CornerRadius);
        Shell.Background = new SolidColorBrush(Color.FromArgb(alpha, bg.R, bg.G, bg.B));
        Shell.BorderBrush = new SolidColorBrush(Color.FromArgb(
            _touchFlickTargetPreview ? (byte)245 : (_peekMode ? (byte)220 : (byte)90),
            accent.R, accent.G, accent.B));
        Shell.BorderThickness = new Thickness(_touchFlickTargetPreview ? 2 : 1);
        HeaderPanel.Background = new SolidColorBrush(Color.FromArgb(headerAlpha, bg.R, bg.G, bg.B));
        HeaderPanel.BorderBrush = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));
        BodyPanel.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Max(20, alpha - 24), bg.R, bg.G, bg.B));
        RollButton.Content = Model.RolledUp ? "⌄" : "⌃";
        RollButton.IsEnabled = _settings.EnableRollup;
        RollButton.Opacity = _settings.EnableRollup ? 1 : 0.38;
        ResizeGrip.Visibility = Model.RolledUp || Model.Locked || !CapabilityAllowed("Layout.MoveResizeGroups") ? Visibility.Collapsed : Visibility.Visible;
        Title = $"CyanDeck - {Model.Title}";
        UpdateTitlebarPlacement();
        ApplyTitlebarMode(DesktopPointerHover.IsActuallyOver(this));
        UpdateHeaderControls(DesktopPointerHover.IsActuallyOver(this));
        RefreshWallpaperBlur();
        _chameleonActivationBridge?.Refresh();
    }

    private void ApplyTitlebarMode(bool pointerInside)
    {
        if (Model.RolledUp)
        {
            SetTitlebarExtent(42);
            HeaderContent.Visibility = Visibility.Visible;
            HeaderPanel.Opacity = 1;
            RefreshInactiveTabControlBridges();
            return;
        }

        switch (Model.TitlebarMode)
        {
            case DeckTitlebarMode.Mouseover:
                SetTitlebarExtent(pointerInside ? 42 : 9);
                HeaderContent.Visibility = pointerInside ? Visibility.Visible : Visibility.Collapsed;
                HeaderPanel.Opacity = pointerInside ? 1 : 0.16;
                break;
            case DeckTitlebarMode.Hidden:
                var revealHiddenHeader = (Model.Tabs.Count >= 2 || SelectedTab?.Kind == DeckTabKind.FolderPortal)
                    && (pointerInside || HeaderContent.IsKeyboardFocusWithin);
                SetTitlebarExtent(revealHiddenHeader ? 42 : 8);
                HeaderContent.Visibility = revealHiddenHeader ? Visibility.Visible : Visibility.Collapsed;
                HeaderPanel.Opacity = revealHiddenHeader ? 1 : 0.08;
                break;
            default:
                SetTitlebarExtent(42);
                HeaderContent.Visibility = Visibility.Visible;
                HeaderPanel.Opacity = 1;
                break;
        }
        RefreshInactiveTabControlBridges();
    }

    private void SetTitlebarExtent(double extent)
    {
        if (_titlebarAtBottom)
        {
            HeaderRow.Height = new GridLength(0);
            BottomHeaderRow.Height = new GridLength(extent);
        }
        else
        {
            HeaderRow.Height = new GridLength(extent);
            BottomHeaderRow.Height = new GridLength(0);
        }
    }

    private void UpdateTitlebarPlacement()
    {
        var placeBottom = false;
        if (Model.MoveTitlebarToBottomNearTop && !Model.RolledUp && IsLoaded)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var center = PointToScreen(new Point(Math.Max(1, ActualWidth) / 2, Math.Max(1, ActualHeight) / 2));
                var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
                var workTop = screen.WorkingArea.Top / dpi.DpiScaleY;
                placeBottom = Top <= workTop + 18;
            }
            catch { }
        }

        if (_titlebarAtBottom == placeBottom) return;
        _titlebarAtBottom = placeBottom;
        Grid.SetRow(HeaderPanel, placeBottom ? 4 : 0);
        HeaderPanel.BorderThickness = placeBottom ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1);
        ApplyTitlebarMode(DesktopPointerHover.IsActuallyOver(this));
    }

    private void ApplyRolledState(bool persist)
    {
        var collapsed = Model.RolledUp && !_rollupPreviewOpen;
        if (collapsed)
        {
            if (ActualHeight > 60) Model.ExpandedHeight = ActualHeight;
            BodyRow.Height = new GridLength(0);
            BodyPanel.Visibility = Visibility.Collapsed;
            PortalNavPanel.Visibility = Visibility.Collapsed;
            ResizeMode = ResizeMode.NoResize;
            ResizeGrip.Visibility = Visibility.Collapsed;
            Height = 42;
        }
        else
        {
            BodyPanel.Visibility = Visibility.Visible;
            BodyRow.Height = new GridLength(1, GridUnitType.Star);
            UpdatePortalNavigation(SelectedTab);
            ResizeMode = Model.RolledUp || Model.Locked ? ResizeMode.NoResize : ResizeMode.CanResize;
            ResizeGrip.Visibility = Model.RolledUp || Model.Locked || !CapabilityAllowed("Layout.MoveResizeGroups") ? Visibility.Collapsed : Visibility.Visible;
            Height = Math.Max(140, Model.ExpandedHeight > 60 ? Model.ExpandedHeight : Model.Height);
        }
        ApplyRollupDockPosition();
        RefreshAppearance();
        if (persist) ModelChanged?.Invoke();
    }

    private void ApplyRollupDockPosition()
    {
        if (Model.RollupDockEdge == DeckRollupDockEdge.None || !IsLoaded || !TryGetWorkingArea(out var work)) return;
        _applyingLayout = true;
        try
        {
            var width = Math.Max(1, double.IsNaN(Width) ? Math.Max(1, ActualWidth) : Width);
            var height = Math.Max(1, double.IsNaN(Height) ? Math.Max(1, ActualHeight) : Height);
            Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - width));
            Top = Model.RollupDockEdge == DeckRollupDockEdge.Top
                ? work.Top
                : Math.Max(work.Top, work.Bottom - height);
            Model.X = Left;
            Model.Y = Top;
        }
        finally { _applyingLayout = false; }
    }

    private bool TryAutoRollupAtMonitorEdge()
    {
        if (!_settings.EnableRollup || !_settings.EnableRollupEdgeDock || Model.Locked || !IsLoaded || !TryGetWorkingArea(out var work)) return false;
        const double threshold = 18;
        var height = Math.Max(1, ActualHeight > 0 ? ActualHeight : Height);
        var nearTop = Math.Abs(Top - work.Top) <= threshold;
        var nearBottom = Math.Abs((Top + height) - work.Bottom) <= threshold;
        if (!nearTop && !nearBottom) return false;

        if (!Model.RolledUp && height > 60) Model.ExpandedHeight = height;
        Model.RollupDockEdge = nearTop ? DeckRollupDockEdge.Top : DeckRollupDockEdge.Bottom;
        Model.RolledUp = true;
        _rollupPreviewOpen = false;
        ApplyRolledState(false);
        Model.X = Left;
        Model.Y = Top;
        return true;
    }

    private void SetRollupPreview(bool open)
    {
        if (!Model.RolledUp) return;
        if (_rollupPreviewOpen == open) return;
        _rollupPreviewOpen = open;
        ApplyRolledState(false);
    }

    private void ToggleRollUp()
    {
        if (!_settings.EnableRollup) return;
        _rollupHoverTimer?.Stop();
        _rollupPreviewOpen = false;
        Model.RolledUp = !Model.RolledUp;
        if (Model.RolledUp)
        {
            SearchBox.Text = "";
            SearchPanel.Visibility = Visibility.Collapsed;
        }
        ApplyRolledState(true);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (e.ClickCount >= 2)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0
                && SelectedTab?.Kind == DeckTabKind.FolderPortal
                && !string.IsNullOrWhiteSpace(SelectedTab.PortalPath))
            {
                Open(SelectedTab.PortalPath);
            }
            else if (_settings.EnableRollup && _settings.EnableRollupDoubleClick)
            {
                ToggleRollUp();
            }
            else
            {
                return;
            }
            e.Handled = true;
            return;
        }
        if (Model.RolledUp && Model.RollupActivation == DeckRollupActivation.Click && e.LeftButton == MouseButtonState.Pressed)
        {
            CancelPendingDeckMove();
            SetRollupPreview(!_rollupPreviewOpen);
            e.Handled = true;
            return;
        }
    }

    private void Fence_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed || e.ClickCount != 1) return;
        if (_peekMode || Model.Locked || !CapabilityAllowed("Layout.MoveResizeGroups")) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) != 0) return;
        if (!CanStartDeckMoveFrom(e.OriginalSource as DependencyObject)) return;

        _movePending = true;
        _moveStartScreen = PointToScreen(e.GetPosition(this));
        _moveStartLeft = Left;
        _moveStartTop = Top;
    }

    private void Fence_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((!_movePending && !_moving) || e.LeftButton != MouseButtonState.Pressed) return;
        var now = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var dx = (now.X - _moveStartScreen.X) / dpi.DpiScaleX;
        var dy = (now.Y - _moveStartScreen.Y) / dpi.DpiScaleY;

        if (!_moving)
        {
            var minX = SystemParameters.MinimumHorizontalDragDistance;
            var minY = SystemParameters.MinimumVerticalDragDistance;
            if (Math.Abs(dx) < minX && Math.Abs(dy) < minY) return;
            _movePending = false;
            _moving = true;
            if (!Model.RolledUp) Model.RollupDockEdge = DeckRollupDockEdge.None;
            Mouse.Capture(this, CaptureMode.SubTree);
            Cursor = Cursors.SizeAll;
        }

        var left = _moveStartLeft + dx;
        var top = _moveStartTop + dy;
        if (_settings.EnableSmartGroupSnapping && _resolveMovePosition is not null)
        {
            var snapped = _resolveMovePosition(this, left, top);
            left = snapped.X;
            top = snapped.Y;
        }
        Left = left;
        Top = top;
        if (_settings.EnableSmartGroupSnapping) SnapToMonitorEdges();
        e.Handled = true;
    }

    private void Fence_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_movePending && !_moving) return;
        var moved = _moving;
        CompleteDeckMove();
        if (moved) e.Handled = true;
    }

    private void Fence_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_moving || _movePending) CompleteDeckMove();
    }

    private void CompleteDeckMove()
    {
        var moved = _moving;
        _movePending = false;
        _moving = false;
        Cursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!moved) return;
        if (!TryAutoRollupAtMonitorEdge()) SnapToMonitorEdges();
        Model.X = Left;
        Model.Y = Top;
        ModelChanged?.Invoke();
        RefreshWallpaperBlur();
        MoveCompleted?.Invoke(this);
    }

    private void CancelPendingDeckMove()
    {
        if (_moving) return;
        _movePending = false;
    }

    private bool CanStartDeckMoveFrom(DependencyObject? source)
    {
        if (source is null) return false;
        if (FindAncestor<ButtonBase>(source) is not null) return false;
        if (FindAncestor<TextBoxBase>(source) is not null) return false;
        if (FindAncestor<ListBoxItem>(source) is not null) return false;
        if (FindAncestor<Thumb>(source) is not null) return false;
        if (FindAncestor<ScrollBar>(source) is not null) return false;
        if (FindAncestor<Slider>(source) is not null) return false;
        return true;
    }


    private void SnapToMonitorEdges()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
            var wa = screen.WorkingArea;
            var left = wa.Left / dpi.DpiScaleX;
            var top = wa.Top / dpi.DpiScaleY;
            var right = wa.Right / dpi.DpiScaleX;
            var bottom = wa.Bottom / dpi.DpiScaleY;
            const double snap = 14;
            var gap = _settings.EnableSmartGroupSnapping ? Math.Clamp(_settings.GroupSnapGap, 0, 64) : 0;
            if (Math.Abs(Left - left) <= snap) Left = left + gap;
            if (Math.Abs(Top - top) <= snap) Top = top + gap;
            if (Math.Abs((Left + ActualWidth) - right) <= snap) Left = right - gap - ActualWidth;
            if (!Model.RolledUp && Math.Abs((Top + ActualHeight) - bottom) <= snap) Top = bottom - gap - ActualHeight;
        }
        catch { }
    }

    private void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resizeIconMovementToken = null;
        _resizeIconMovementTabId = null;
        var tab = SelectedTab;
        if (tab is null || !_settings.AnimateIconsWhenMoved || Model.RolledUp || Model.Locked || !CapabilityAllowed("Layout.MoveResizeGroups")) return;
        _resizeIconMovementTabId = tab.Id;
        _resizeIconMovementToken = _iconMovementAnimationCoordinator.Begin(
            CaptureIconMovementAnchors(tab),
            IconMovementAnimationContext.ResizeCompleted);
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Model.RolledUp || Model.Locked || !CapabilityAllowed("Layout.MoveResizeGroups")) return;
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(140, Height + e.VerticalChange);
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Model.RolledUp || Model.Locked || !CapabilityAllowed("Layout.MoveResizeGroups"))
        {
            CancelIconMovementAnimationState(resetTransforms: true);
            return;
        }
        if (_settings.SnapGroupSizeToIconMultiples) SnapGroupSizeToIconCells();
        Model.Width = ActualWidth;
        Model.Height = ActualHeight;
        Model.ExpandedHeight = ActualHeight;
        ModelChanged?.Invoke();
        RefreshWallpaperBlur();
        if (_resizeIconMovementToken is long token && _resizeIconMovementTabId is Guid tabId)
        {
            var batch = new IconMovementBatch(tabId, token);
            CompleteIconMovementBatchAfterLayout(batch);
        }
        _resizeIconMovementToken = null;
        _resizeIconMovementTabId = null;
    }

    private void SnapGroupSizeToIconCells()
    {
        if (SelectedTab?.ViewMode != DeckItemViewMode.Grid) return;
        if (SelectedTab.Kind == DeckTabKind.Collection && Model.IconLayoutMode == DeckIconLayoutMode.Free) return;

        var metrics = GetGridTileMetrics();
        var bodyWidth = Math.Max(metrics.TileWidth, BodyPanel.ActualWidth - BodyPanel.Padding.Left - BodyPanel.Padding.Right);
        var bodyHeight = Math.Max(metrics.TileHeight, BodyPanel.ActualHeight - BodyPanel.Padding.Top - BodyPanel.Padding.Bottom);
        var columns = Math.Max(1, (int)Math.Round(bodyWidth / metrics.TileWidth));
        var rows = Math.Max(1, (int)Math.Round(bodyHeight / metrics.TileHeight));
        var chromeWidth = Math.Max(0, ActualWidth - bodyWidth);
        var chromeHeight = Math.Max(0, ActualHeight - bodyHeight);

        Width = Math.Max(MinWidth, chromeWidth + columns * metrics.TileWidth);
        Height = Math.Max(140, chromeHeight + rows * metrics.TileHeight);
    }

    private void RollButton_Click(object sender, RoutedEventArgs e) => ToggleRollUp();

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildMenu();
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private static ContextMenu CreateThemedContextMenu() => FenceContextMenuTheme.Create();




    private ContextMenu BuildMenu()
    {
        var menu = CreateThemedContextMenu();
        PopulateGroupContextMenu(menu);
        return menu;
    }

    private void OpenRuleEditor()
    {
        if (!CapabilityAllowed("Automation.EditRules")) return;
        var editor = new RuleEditorWindow(Model, () =>
        {
            ModelChanged?.Invoke();
            AutomationRulesChanged?.Invoke();
        }) { Owner = this };
        editor.ShowDialog();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => ShowSearch();

    private void SearchClose_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchPanel.Visibility = Visibility.Collapsed;
        if (Model.RolledUp) SetRollupPreview(false);
        Focus();
        RefreshItems(IconMovementAnimationContext.SearchFilterChanged);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshItems(IconMovementAnimationContext.SearchFilterChanged);
    }

    private void ShowSearch()
    {
        if (Model.RolledUp && !_rollupPreviewOpen) SetRollupPreview(true);
        SearchPanel.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private bool MatchesSearch(ItemTile tile)
    {
        var q = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(q)) return true;
        return tile.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || tile.Path.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || tile.Type.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateSearchCount(int shown, int total)
    {
        if (SearchCountText is null) return;
        SearchCountText.Text = SearchPanel.Visibility == Visibility.Visible ? $"{shown}/{total}" : "";
    }

    private ItemTile? KeyboardSelectedTile()
        => ItemsList.Visibility == Visibility.Visible ? ItemsList.SelectedItem as ItemTile : _freeSelectedTile;

    private List<ItemTile> GetSelectedTiles()
    {
        if (ItemsList.Visibility == Visibility.Visible)
            return ItemsList.SelectedItems.Cast<ItemTile>().ToList();
        var selected = new List<ItemTile>();
        foreach (var id in _freeSelectedIds)
            if (_freeBorders.TryGetValue(id, out var border) && border.Tag is ItemTile tile) selected.Add(tile);
        return selected;
    }

    private void SelectAllItems()
    {
        if (ItemsList.Visibility == Visibility.Visible)
        {
            ItemsList.SelectAll();
            return;
        }
        _freeSelectedIds.Clear();
        foreach (var pair in _freeBorders)
        {
            _freeSelectedIds.Add(pair.Key);
            if (pair.Value.Tag is ItemTile tile) UpdateFreeTileSelectionVisual(pair.Value, tile);
        }
        _freeSelectedTile = _freeBorders.Values.Select(b => b.Tag).OfType<ItemTile>().FirstOrDefault();
        _freeSelectedBorder = _freeSelectedTile is null ? null : _freeBorders.GetValueOrDefault(_freeSelectedTile.Source.Id);
    }


    // XAML event hook stays stable; keyboard behavior is isolated in a separate partial.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        => HandleWindowKeyboardCommand(e);

    private void AddRule(RuleField field, string label, string initial)
    {
        if (!CapabilityAllowed("Automation.EditRules")) return;
        var tab = SelectedTab;
        if (tab is null || tab.Kind != DeckTabKind.Collection) return;
        var pattern = TextPrompt.Show(this, "자동 분류 규칙", label, initial);
        if (pattern is null) return;
        tab.Rules.Add(new DeckRule { Field = field, Pattern = pattern });
        ModelChanged?.Invoke();
        AutomationRulesChanged?.Invoke();
    }

    private Stack<string> PortalBackStack(DeckTab tab)
        => _portalBackHistory.TryGetValue(tab.Id, out var stack) ? stack : (_portalBackHistory[tab.Id] = new Stack<string>());

    private Stack<string> PortalForwardStack(DeckTab tab)
        => _portalForwardHistory.TryGetValue(tab.Id, out var stack) ? stack : (_portalForwardHistory[tab.Id] = new Stack<string>());

    private void PushPortalHistory(Stack<string> stack, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        stack.Push(path);
        var limit = Math.Clamp(_settings.PortalHistoryLimit, 5, 200);
        if (stack.Count <= limit) return;
        var keep = stack.Take(limit).Reverse().ToArray();
        stack.Clear();
        foreach (var value in keep) stack.Push(value);
    }

    private static string PortalViewKey(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
    }

    private static DeckPortalViewState CapturePortalView(DeckTab tab)
        => new()
        {
            ViewMode = tab.ViewMode,
            ListOrientation = tab.ListOrientation,
            ShowDetailsHeader = tab.ShowDetailsHeader,
            DetailsColumns = DeckDetailsColumnState.Normalize(tab.DetailsColumns).Select(x => x.Clone()).ToList()
        };

    private static void ApplyPortalViewState(DeckTab tab, DeckPortalViewState state)
    {
        tab.ViewMode = state.ViewMode;
        tab.ListOrientation = state.ListOrientation;
        tab.ShowDetailsHeader = state.ShowDetailsHeader;
        tab.DetailsColumns = DeckDetailsColumnState.Normalize(state.DetailsColumns).Select(x => x.Clone()).ToList();
    }

    private static void RememberPortalView(DeckTab tab, string? path)
    {
        if (tab.Kind != DeckTabKind.FolderPortal || string.IsNullOrWhiteSpace(path)) return;
        tab.PortalViewStates ??= new Dictionary<string, DeckPortalViewState>(StringComparer.OrdinalIgnoreCase);
        var key = PortalViewKey(path);
        if (!tab.PortalViewStates.ContainsKey(key) && tab.PortalViewStates.Count >= 256)
        {
            var oldest = tab.PortalViewStates.Keys.FirstOrDefault();
            if (oldest is not null) tab.PortalViewStates.Remove(oldest);
        }
        tab.PortalViewStates[key] = CapturePortalView(tab);
    }

    private static void PreparePortalTargetView(DeckTab tab, string targetPath)
    {
        tab.PortalViewStates ??= new Dictionary<string, DeckPortalViewState>(StringComparer.OrdinalIgnoreCase);
        var key = PortalViewKey(targetPath);
        if (tab.InheritPortalViewMode)
        {
            tab.PortalViewStates[key] = CapturePortalView(tab);
            return;
        }

        if (tab.PortalViewStates.TryGetValue(key, out var saved))
        {
            ApplyPortalViewState(tab, saved);
        }
        else
        {
            tab.ViewMode = DeckItemViewMode.Grid;
            tab.ListOrientation = DeckListOrientation.Vertical;
            tab.ShowDetailsHeader = true;
            tab.DetailsColumns = DeckDetailsColumnState.CreateDefault();
            tab.PortalViewStates[key] = CapturePortalView(tab);
        }
    }

    private void NavigatePortalTo(string path, bool recordHistory = true)
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.FolderPortal || !Directory.Exists(path)) return;
        var target = Path.GetFullPath(path);
        var current = string.IsNullOrWhiteSpace(tab.PortalPath) ? null : Path.GetFullPath(tab.PortalPath);
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return;
        if (current is not null) RememberPortalView(tab, current);
        if (recordHistory && current is not null)
        {
            PushPortalHistory(PortalBackStack(tab), current);
            PortalForwardStack(tab).Clear();
        }
        tab.PortalRootPath ??= current ?? target;
        PreparePortalTargetView(tab, target);
        tab.PortalPath = target;
        TrySyncPortalFromExplorer(tab, force: false);
        _portalCacheDirty = true;
        RefreshItems(IconMovementAnimationContext.InitialMaterialization);
        ModelChanged?.Invoke();
    }

    private bool TrySyncPortalFromExplorer(DeckTab tab, bool force)
    {
        if (tab.Kind != DeckTabKind.FolderPortal || string.IsNullOrWhiteSpace(tab.PortalPath)) return false;
        if (!force && !tab.SyncOpenExplorerView) return false;
        try
        {
            bool presentationChanged = false;
            bool iconSizeChanged = false;
            bool detailsChanged = false;

            // v6.7 remains authoritative for view mode, icon size, and sort.
            var coordinator = new FolderPortalExplorerViewSyncCoordinator();
            var result = coordinator.TryCreatePlan(tab.PortalPath, new FolderPortalExplorerViewSyncOptions(
                Enabled: true,
                ImportViewMode: tab.SyncExplorerViewMode,
                ImportIconSize: tab.SyncExplorerIconSize,
                ImportSort: tab.SyncExplorerSort));
            if (result.WasApplied && result.Plan is { } plan)
            {
                if (plan.View is FolderPortalImportedView view)
                {
                    var importedView = view switch
                    {
                        FolderPortalImportedView.List => DeckItemViewMode.List,
                        FolderPortalImportedView.Details => DeckItemViewMode.Details,
                        _ => DeckItemViewMode.Grid,
                    };
                    if (tab.ViewMode != importedView)
                    {
                        tab.ViewMode = importedView;
                        presentationChanged = true;
                    }
                }

                if (plan.IconSize is int iconSize)
                {
                    var importedSize = Math.Clamp(iconSize, 24, 128);
                    if (Math.Abs(Model.IconSize - importedSize) > 0.01)
                    {
                        Model.IconSize = importedSize;
                        iconSizeChanged = true;
                        presentationChanged = true;
                    }
                }

                if (plan.Sort is FolderPortalImportedSort sort)
                {
                    var importedSort = sort switch
                    {
                        FolderPortalImportedSort.Type => DeckSortMode.Type,
                        FolderPortalImportedSort.Modified => DeckSortMode.Modified,
                        FolderPortalImportedSort.Size => DeckSortMode.Size,
                        _ => DeckSortMode.Name,
                    };
                    if (tab.Sort != importedSort || tab.SortDescending != plan.SortDescending)
                    {
                        tab.Sort = importedSort;
                        tab.SortDescending = plan.SortDescending;
                        presentationChanged = true;
                    }
                }
            }

            // v7.17 is gated by the parent live-Explorer toggle even for the explicit sync command.
            var detailsCoordinator = new FolderPortalExplorerDetailsColumnSyncCoordinator();
            var detailsResult = detailsCoordinator.TryCreatePlan(
                tab.PortalPath,
                new FolderPortalExplorerDetailsColumnSyncOptions(
                    Enabled: tab.SyncOpenExplorerView,
                    ImportVisibleSetAndOrder: tab.SyncExplorerDetailsColumns,
                    ImportWidths: tab.SyncExplorerDetailsColumnWidths));

            if (detailsResult.WasApplied && detailsResult.Plan is { } detailsPlan)
            {
                detailsChanged = ApplyExplorerDetailsColumnPlan(tab, detailsPlan);
                int mappedColumns = detailsPlan.Columns.Count(x => x.IsVisible);
                int importedWidths = detailsPlan.Columns.Count(x => x.WidthDip.HasValue);
                DiagnosticsLog.Info($"Folder Portal Explorer Details sync: enabled=true; status={detailsResult.ReadStatus}; mapped={mappedColumns}; widths={importedWidths}; nameForced={detailsPlan.NameWasForcedVisible}");
            }
            else if (tab.SyncOpenExplorerView && (tab.SyncExplorerDetailsColumns || tab.SyncExplorerDetailsColumnWidths))
            {
                DiagnosticsLog.Info($"Folder Portal Explorer Details sync: enabled=true; status={detailsResult.ReadStatus}; mapped=0; widths=0; nameForced=false");
            }

            if (!presentationChanged && !detailsChanged) return false;

            RememberPortalView(tab, tab.PortalPath);
            if (iconSizeChanged) _portalCacheDirty = true;

            if (presentationChanged)
            {
                // View/sort-only changes reuse the Portal tile cache. Only an icon-size change
                // invalidates tile visuals and causes the existing async enumeration path to run.
                RefreshItems(IconMovementAnimationContext.ViewModeChanged);
            }
            else
            {
                RefreshDetailsColumnLayoutPreservingSelectionAndScroll(tab);
            }

            ModelChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Folder Portal Explorer-view sync failed", ex);
            return false;
        }
    }

    private bool ApplyExplorerDetailsColumnPlan(DeckTab tab, FolderPortalExplorerDetailsColumnImportPlan plan)
    {
        var before = DeckDetailsColumnState.Normalize(tab.DetailsColumns);
        var working = before.Select(x => x.Clone()).ToDictionary(x => x.Kind);

        foreach (FolderPortalDetailsColumnLayoutEntry entry in plan.Columns)
        {
            var kind = entry.Column switch
            {
                FolderPortalDetailsColumn.Modified => DeckDetailsColumnKind.Modified,
                FolderPortalDetailsColumn.Type => DeckDetailsColumnKind.Type,
                FolderPortalDetailsColumn.Size => DeckDetailsColumnKind.Size,
                _ => DeckDetailsColumnKind.Name,
            };
            if (!working.TryGetValue(kind, out var state)) continue;

            if (tab.SyncExplorerDetailsColumns)
            {
                state.IsVisible = entry.IsVisible;
                if (entry.IsVisible) state.DisplayOrder = entry.DisplayOrder;
            }

            if (tab.SyncExplorerDetailsColumnWidths && entry.WidthDip is double width)
                state.WidthDip = Math.Clamp(width, 48, 640);
        }

        var after = DeckDetailsColumnState.Normalize(working.Values);
        bool changed = before.Count != after.Count || before.Zip(after).Any(pair =>
            pair.First.Kind != pair.Second.Kind ||
            pair.First.IsVisible != pair.Second.IsVisible ||
            pair.First.DisplayOrder != pair.Second.DisplayOrder ||
            pair.First.WidthDip != pair.Second.WidthDip);

        if (changed) tab.DetailsColumns = after;
        return changed;
    }

    private void ApplyDetailsColumnLayout(DeckTab tab)
    {
        tab.DetailsColumns = DeckDetailsColumnState.Normalize(tab.DetailsColumns);
        DetailsColumnsPanel.SetLayout(ItemsList, tab.DetailsColumns);
        DetailsColumnsPanel.SetLayout(DetailsHeaderPanel, tab.DetailsColumns);
    }

    private void RefreshDetailsColumnLayoutPreservingSelectionAndScroll(DeckTab tab)
    {
        var selectedIds = ItemsList.SelectedItems
            .OfType<ItemTile>()
            .Select(x => x.Source.Id)
            .ToHashSet();
        var scroll = FindVisualChild<ScrollViewer>(ItemsList);
        double verticalOffset = scroll?.VerticalOffset ?? 0;
        double horizontalOffset = scroll?.HorizontalOffset ?? 0;

        ApplyDetailsColumnLayout(tab);
        UpdateDetailsHeader(tab);
        ItemsList.InvalidateMeasure();
        ItemsList.UpdateLayout();

        if (selectedIds.Count > 0)
        {
            ItemsList.SelectedItems.Clear();
            foreach (var tile in ItemsList.Items.OfType<ItemTile>())
            {
                if (selectedIds.Contains(tile.Source.Id)) ItemsList.SelectedItems.Add(tile);
            }
        }

        if (scroll is not null)
        {
            scroll.ScrollToHorizontalOffset(horizontalOffset);
            scroll.ScrollToVerticalOffset(verticalOffset);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OpenPortalFolder(string path)
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.FolderPortal) { Open(path); return; }
        if (tab.NavigateFoldersInPortal) NavigatePortalTo(path);
        else Open(path);
    }

    private void NavigatePortalBack()
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.FolderPortal) return;
        var back = PortalBackStack(tab);
        while (back.Count > 0)
        {
            var target = back.Pop();
            if (!Directory.Exists(target)) continue;
            if (!string.IsNullOrWhiteSpace(tab.PortalPath))
            {
                RememberPortalView(tab, tab.PortalPath);
                PushPortalHistory(PortalForwardStack(tab), tab.PortalPath);
            }
            PreparePortalTargetView(tab, target);
            tab.PortalPath = target;
            TrySyncPortalFromExplorer(tab, force: false);
            _portalCacheDirty = true;
            RefreshItems(IconMovementAnimationContext.InitialMaterialization);
            ModelChanged?.Invoke();
            return;
        }
        UpdatePortalNavigation(tab);
    }

    private void NavigatePortalForward()
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.FolderPortal) return;
        var forward = PortalForwardStack(tab);
        while (forward.Count > 0)
        {
            var target = forward.Pop();
            if (!Directory.Exists(target)) continue;
            if (!string.IsNullOrWhiteSpace(tab.PortalPath))
            {
                RememberPortalView(tab, tab.PortalPath);
                PushPortalHistory(PortalBackStack(tab), tab.PortalPath);
            }
            PreparePortalTargetView(tab, target);
            tab.PortalPath = target;
            TrySyncPortalFromExplorer(tab, force: false);
            _portalCacheDirty = true;
            RefreshItems(IconMovementAnimationContext.InitialMaterialization);
            ModelChanged?.Invoke();
            return;
        }
        UpdatePortalNavigation(tab);
    }

    private void NavigatePortalUp()
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.FolderPortal || string.IsNullOrWhiteSpace(tab.PortalPath)) return;
        try
        {
            var parent = Directory.GetParent(tab.PortalPath);
            if (parent is null) return;
            NavigatePortalTo(parent.FullName);
        }
        catch { }
    }

    private void NavigatePortalHome()
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.FolderPortal) return;
        var root = tab.PortalRootPath;
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) NavigatePortalTo(root);
    }

    private void UpdatePortalNavigation(DeckTab? tab)
    {
        if (PortalNavPanel is null) return;
        var portal = tab?.Kind == DeckTabKind.FolderPortal;
        PortalNavPanel.Visibility = portal ? Visibility.Visible : Visibility.Collapsed;
        if (!portal || tab is null) return;
        tab.PortalRootPath ??= tab.PortalPath;
        PortalPathText.Text = tab.PortalPath ?? "폴더를 사용할 수 없습니다";
        PortalPathText.ToolTip = tab.PortalPath;
        PortalBackButton.IsEnabled = PortalBackStack(tab).Any(Directory.Exists);
        PortalForwardButton.IsEnabled = PortalForwardStack(tab).Any(Directory.Exists);
        PortalUpButton.IsEnabled = !string.IsNullOrWhiteSpace(tab.PortalPath) && Directory.GetParent(tab.PortalPath) is not null;
        PortalHomeButton.IsEnabled = !string.IsNullOrWhiteSpace(tab.PortalRootPath) && Directory.Exists(tab.PortalRootPath) &&
                                     !string.Equals(tab.PortalRootPath, tab.PortalPath, StringComparison.OrdinalIgnoreCase);
    }

    private void PortalBack_Click(object sender, RoutedEventArgs e) => NavigatePortalBack();
    private void PortalForward_Click(object sender, RoutedEventArgs e) => NavigatePortalForward();
    private void PortalUp_Click(object sender, RoutedEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) NavigatePortalHome();
        else NavigatePortalUp();
    }
    private void PortalUp_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        NavigatePortalHome();
        e.Handled = true;
    }
    private void PortalHome_Click(object sender, RoutedEventArgs e) => NavigatePortalHome();

    private void BodyPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || e.ChangedButton != MouseButton.Left) return;
        if (SelectedTab?.Kind != DeckTabKind.FolderPortal || string.IsNullOrWhiteSpace(SelectedTab.PortalPath)) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        Open(SelectedTab.PortalPath);
        e.Handled = true;
    }

    private void AddCollectionTab()
    {
        if (!CapabilityAllowed("Settings.EditGroups")) return;
        var text = TextPrompt.Show(this, "새 탭", "탭 이름", "항목");
        if (text is null) return;
        Model.Tabs.Add(new DeckTab { Title = text });
        Model.SelectedTabIndex = Model.Tabs.Count - 1;
        RebuildTabs();
        RefreshItems(IconMovementAnimationContext.InitialMaterialization);
        ModelChanged?.Invoke();
    }

    private void AddPortalTab()
    {
        if (!CapabilityAllowed("FolderPortal.Edit")) return;
        using var dialog = new Forms.FolderBrowserDialog { Description = "표시할 폴더를 선택하세요", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var title = Path.GetFileName(dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(title)) title = dialog.SelectedPath;
        Model.Tabs.Add(new DeckTab { Title = title, Kind = DeckTabKind.FolderPortal, PortalPath = dialog.SelectedPath, PortalRootPath = dialog.SelectedPath });
        Model.SelectedTabIndex = Model.Tabs.Count - 1;
        RebuildTabs();
        RefreshItems(IconMovementAnimationContext.InitialMaterialization);
        ModelChanged?.Invoke();
    }

    private void MoveCurrentTab(int delta)
    {
        var tab = SelectedTab;
        if (tab is null) return;
        MoveTabById(tab.Id, delta);
    }

    private void DetachCurrentTab()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        DetachTabById(tab.Id);
    }

    private void RemoveCurrentTab()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        RemoveTabById(tab.Id);
    }

    private void SetOpacity(double value)
    {
        Model.Opacity = value;
        RefreshAppearance();
        ModelChanged?.Invoke();
    }

    private void SetTint(double value)
    {
        Model.IconTint = Model.Accent;
        Model.IconTintStrength = value;
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void SetIconSize(double value)
    {
        Model.IconSize = Math.Clamp(value, 24, 128);
        RefreshItems(IconMovementAnimationContext.ManualArrange);
        ModelChanged?.Invoke();
    }

    private void SetIconSpacing(double horizontal, double vertical)
    {
        Model.IconSpacingX = Math.Clamp(horizontal, 0, 80);
        Model.IconSpacingY = Math.Clamp(vertical, 0, 80);
        RefreshItems(IconMovementAnimationContext.ManualArrange);
        ModelChanged?.Invoke();
    }

    private void SetIconLabelMode(DeckIconLabelMode mode)
    {
        Model.IconLabelMode = mode;
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void SetItemViewMode(DeckItemViewMode mode)
    {
        var tab = SelectedTab;
        if (tab is null) return;
        tab.ViewMode = mode;
        RememberPortalView(tab, tab.PortalPath);
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void SetListOrientation(DeckListOrientation orientation)
    {
        var tab = SelectedTab;
        if (tab is null) return;
        tab.ListOrientation = orientation;
        RememberPortalView(tab, tab.PortalPath);
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void ToggleDetailsHeader()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        tab.ShowDetailsHeader = !tab.ShowDetailsHeader;
        RememberPortalView(tab, tab.PortalPath);
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void SetDetailsSort(DeckSortMode mode)
    {
        var tab = SelectedTab;
        if (tab is null) return;
        if (tab.Sort == mode) tab.SortDescending = !tab.SortDescending;
        else
        {
            tab.Sort = mode;
            tab.SortDescending = false;
        }
        RefreshItems(IconMovementAnimationContext.SortOrArrange);
        ModelChanged?.Invoke();
    }

    private void UpdateDetailsHeader(DeckTab tab)
    {
        if (DetailsNameButton is null) return;
        static string Caption(string label, DeckTab tab, DeckSortMode mode)
            => tab.Sort == mode ? $"{label} {(tab.SortDescending ? "▼" : "▲")}" : label;
        DetailsNameButton.Content = Caption("이름", tab, DeckSortMode.Name);
        DetailsModifiedButton.Content = Caption("수정한 날짜", tab, DeckSortMode.Modified);
        DetailsTypeButton.Content = Caption("형식", tab, DeckSortMode.Type);
        DetailsSizeButton.Content = Caption("크기", tab, DeckSortMode.Size);
    }

    private void DetailsName_Click(object sender, RoutedEventArgs e) => SetDetailsSort(DeckSortMode.Name);
    private void DetailsModified_Click(object sender, RoutedEventArgs e) => SetDetailsSort(DeckSortMode.Modified);
    private void DetailsType_Click(object sender, RoutedEventArgs e) => SetDetailsSort(DeckSortMode.Type);
    private void DetailsSize_Click(object sender, RoutedEventArgs e) => SetDetailsSort(DeckSortMode.Size);

    private void SetTitleFont(string family)
    {
        Model.TitleFontFamily = family;
        RebuildTabs();
        ModelChanged?.Invoke();
    }

    private void SetTitleFontSize(double size)
    {
        Model.TitleFontSize = Math.Clamp(size, 9, 24);
        RebuildTabs();
        ModelChanged?.Invoke();
    }

    private void SetTitleColor(string color)
    {
        Model.TitleForeground = color;
        RebuildTabs();
        ModelChanged?.Invoke();
    }

    private void SetIconLayout(DeckIconLayoutMode mode)
    {
        Model.IconLayoutMode = mode;
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void SetRollupActivation(DeckRollupActivation mode)
    {
        Model.RollupActivation = mode;
        _rollupHoverTimer?.Stop();
        if (Model.RolledUp) SetRollupPreview(false);
        ModelChanged?.Invoke();
    }

    private void PackFreePositions()
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection || Model.IconLayoutMode != DeckIconLayoutMode.Free) return;
        var metrics = GetGridTileMetrics();
        var cellWidth = metrics.TileWidth + 4;
        var cellHeight = metrics.TileHeight + 4;
        var availableWidth = Math.Max(cellWidth, (FreeScroll.ActualWidth > 0 ? FreeScroll.ActualWidth : ActualWidth) - 24);
        var columns = Math.Max(1, (int)Math.Floor(availableWidth / cellWidth));
        var ordered = tab.Items.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            ordered[index].PositionX = 8 + column * cellWidth;
            ordered[index].PositionY = 8 + row * cellHeight;
        }
        RefreshItems(IconMovementAnimationContext.ManualArrange);
        ModelChanged?.Invoke();
    }

    private void AlignSelectedFreeIconsLeft() => AlignSelectedFreeIcons(horizontal: true);

    private void AlignSelectedFreeIconsTop() => AlignSelectedFreeIcons(horizontal: false);

    private void AlignSelectedFreeIcons(bool horizontal)
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection || Model.IconLayoutMode != DeckIconLayoutMode.Free) return;
        var selectedIds = _freeSelectedIds.ToHashSet();
        var selected = tab.Items.Where(item => selectedIds.Contains(item.Id)).ToList();
        if (selected.Count < 2) return;

        if (horizontal)
        {
            var left = selected.Where(i => i.PositionX.HasValue).Select(i => i.PositionX!.Value).DefaultIfEmpty(8).Min();
            foreach (var item in selected) item.PositionX = left;
        }
        else
        {
            var top = selected.Where(i => i.PositionY.HasValue).Select(i => i.PositionY!.Value).DefaultIfEmpty(8).Min();
            foreach (var item in selected) item.PositionY = top;
        }
        RefreshItems(IconMovementAnimationContext.ManualArrange);
        ModelChanged?.Invoke();
    }

    private void ResetFreePositions()
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection) return;
        foreach (var item in tab.Items) { item.PositionX = null; item.PositionY = null; }
        RefreshItems(IconMovementAnimationContext.ManualArrange);
        ModelChanged?.Invoke();
    }

    private void SnapFreePositions()
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection) return;
        var metrics = GetGridTileMetrics();
        var cellWidth = metrics.TileWidth + 4;
        var cellHeight = metrics.TileHeight + 4;
        foreach (var item in tab.Items)
        {
            if (item.PositionX is double x) item.PositionX = Math.Max(0, Math.Round(x / cellWidth) * cellWidth + 6);
            if (item.PositionY is double y) item.PositionY = Math.Max(0, Math.Round(y / cellHeight) * cellHeight + 6);
        }
        RefreshItems(IconMovementAnimationContext.ManualArrange);
        ModelChanged?.Invoke();
    }

    private void SetTabAccent(string accent)
    {
        var tab = SelectedTab;
        if (tab is null) return;
        tab.Accent = accent;
        RebuildTabs();
        ModelChanged?.Invoke();
    }

    private void SetTheme(string background, string accent)
    {
        Model.Background = background;
        Model.Accent = accent;
        if (Model.IconTintStrength > 0) Model.IconTint = accent;
        RefreshAppearance();
        RebuildTabs();
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void SetCornerRadius(double radius)
    {
        Model.CornerRadius = Math.Clamp(radius, 0, 28);
        RefreshAppearance();
        ModelChanged?.Invoke();
    }

    private void CopyAppearance()
    {
        _appearanceClipboard = AppearanceSnapshot.From(Model);
    }

    private void PasteAppearance()
    {
        if (_appearanceClipboard is null) return;
        _appearanceClipboard.ApplyTo(Model);
        UpdateTitlebarPlacement();
        RefreshAppearance();
        RebuildTabs();
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void ResetAppearance()
    {
        AppearanceSnapshot.From(new DeckFence()).ApplyTo(Model);
        UpdateTitlebarPlacement();
        RefreshAppearance();
        RebuildTabs();
        RefreshItems(IconMovementAnimationContext.ViewModeChanged);
        ModelChanged?.Invoke();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (TryHandleTabDrop(e, Model.Tabs.Count)) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) return;
        AddDroppedPaths(SelectedTab, files, (e.KeyStates & DragDropKeyStates.ShiftKey) != 0);
        e.Handled = true;
    }

    private void Header_Drop(object sender, DragEventArgs e)
    {
        if (TryHandleTabDrop(e, Model.Tabs.Count)) return;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement header || header.Tag is not DeckTab tab) return;
        var targetIndex = Model.Tabs.IndexOf(tab);
        if (targetIndex >= 0 && e.Data.GetDataPresent(TabDragFormat))
        {
            // Drop on the left half inserts before the hovered tab; the right half inserts after it.
            // This makes long-press reordering track the pointer instead of always jumping before a tab.
            if (e.GetPosition(header).X >= Math.Max(1, header.ActualWidth) / 2.0) targetIndex++;
        }
        if (TryHandleTabDrop(e, Math.Max(0, targetIndex))) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) return;
        SelectTab(tab);
        AddDroppedPaths(tab, files, (e.KeyStates & DragDropKeyStates.ShiftKey) != 0);
        e.Handled = true;
    }

    private bool TryHandleTabDrop(DragEventArgs e, int targetIndex)
    {
        if (!CapabilityAllowed("Settings.EditGroups")) return false;
        if (!e.Data.GetDataPresent(TabDragFormat)) return false;
        var value = e.Data.GetData(TabDragFormat) as string;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var sourceFenceId) || !Guid.TryParse(parts[1], out var tabId)) return false;

        if (sourceFenceId == Model.Id)
        {
            var tab = Model.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab is not null)
            {
                var oldIndex = Model.Tabs.IndexOf(tab);
                Model.Tabs.Remove(tab);
                if (targetIndex > oldIndex) targetIndex--;
                targetIndex = Math.Clamp(targetIndex, 0, Model.Tabs.Count);
                Model.Tabs.Insert(targetIndex, tab);
                Model.SelectedTabIndex = targetIndex;
                RebuildTabs();
                RefreshItems(IconMovementAnimationContext.InitialMaterialization);
                ModelChanged?.Invoke();
            }
        }
        else
        {
            TabTransferRequested?.Invoke(sourceFenceId, tabId, this, targetIndex);
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        return true;
    }

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CapabilityAllowed("Settings.EditGroups") || e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
        if (sender is not FrameworkElement { Tag: DeckTab tab }) return;
        _tabPressTabId = tab.Id;
        _tabPressStart = e.GetPosition(this);
        _tabPressStartedAt = Environment.TickCount64;
    }

    private void Tab_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _tabPressTabId = null;
        _tabPressStartedAt = 0;
    }

    private void Tab_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!CapabilityAllowed("Settings.EditGroups")) return;
        if (_tabDragInProgress || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement { Tag: DeckTab tab }) return;

        var shiftDrag = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var matchesPress = _tabPressTabId == tab.Id && _tabPressStartedAt > 0;
        if (!matchesPress) return;

        var now = e.GetPosition(this);
        var dx = Math.Abs(now.X - _tabPressStart.X);
        var dy = Math.Abs(now.Y - _tabPressStart.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance && dy < SystemParameters.MinimumVerticalDragDistance) return;

        if (!shiftDrag && Environment.TickCount64 - _tabPressStartedAt < TabLongPressMilliseconds) return;

        // A normal long-press drag reorders/transfers tabs but never tears a tab off merely because
        // the pointer was released outside a target. Shift+drag keeps the legacy detach gesture.
        var allowDetach = shiftDrag;
        _tabPressTabId = null;
        _tabPressStartedAt = 0;
        _tabDragInProgress = true;
        try
        {
            var data = new DataObject();
            data.SetData(TabDragFormat, $"{Model.Id}|{tab.Id}");
            var effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
            if (allowDetach && effect == DragDropEffects.None && Model.Tabs.Contains(tab) && Model.Tabs.Count > 1)
                TabDetachRequested?.Invoke(this, tab, GetCursorScreenDip());
        }
        finally { _tabDragInProgress = false; }
    }

    private Point GetCursorScreenDip()
    {
        var cursor = Forms.Cursor.Position;
        try
        {
            var client = PointFromScreen(new Point(cursor.X, cursor.Y));
            return new Point(Left + client.X, Top + client.Y);
        }
        catch { return new Point(Left + 80, Top + 50); }
    }

    private void AddDroppedPaths(DeckTab? tab, IEnumerable<string> files, bool moveToPortal = false)
    {
        if (tab is null) return;
        if (tab.Kind == DeckTabKind.FolderPortal)
        {
            ImportIntoPortal(tab, files, moveToPortal);
            return;
        }

        var changed = false;
        foreach (var file in files)
        {
            var path = file;
            try { path = Path.GetFullPath(file); } catch { }
            if (tab.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            tab.Items.Add(new DeckItem { Path = path });
            SourcePathDropped?.Invoke(path);
            changed = true;
        }
        if (!changed) return;
        RefreshItems(IconMovementAnimationContext.ItemSetChanged);
        ModelChanged?.Invoke();
    }

    private void ImportIntoPortal(DeckTab tab, IEnumerable<string> sources, bool move)
    {
        if (string.IsNullOrWhiteSpace(tab.PortalPath) || !Directory.Exists(tab.PortalPath)) return;
        var errors = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                var sourceFull = Path.GetFullPath(source);
                var destination = ValidatePortalImport(sourceFull, tab.PortalPath);
                if (File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException($"Already exists: {Path.GetFileName(destination)}");

                if (File.Exists(sourceFull))
                {
                    if (move) File.Move(sourceFull, destination);
                    else File.Copy(sourceFull, destination);
                }
                else if (Directory.Exists(sourceFull))
                {
                    if (move && IsSameVolume(sourceFull, destination)) Directory.Move(sourceFull, destination);
                    else if (move) MoveDirectoryAcrossVolumes(sourceFull, destination);
                    else CopyDirectory(sourceFull, destination);
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        RefreshItems(IconMovementAnimationContext.ItemSetChanged);
        if (errors.Count > 0)
            System.Windows.MessageBox.Show(string.Join(Environment.NewLine, errors.Take(6)), "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// One shared policy for both a Portal drop and its Explorer-style Ctrl+V.
    /// The copy/cut operation remains a real filesystem operation; only unsafe
    /// roots, reparse-point paths, protected trees and recursive destinations fail.
    /// </summary>
    private static string ValidatePortalImport(string source, string portal)
    {
        var sourceFull = Path.GetFullPath(source);
        var portalFull = Path.GetFullPath(portal);
        if (!File.Exists(sourceFull) && !Directory.Exists(sourceFull))
            throw new IOException("원본 파일 또는 폴더가 존재하지 않습니다.");
        if (IsProtectedDeletionTarget(sourceFull))
            throw new IOException("드라이브 루트나 보호된 시스템/사용자 폴더는 포털에 복사·이동할 수 없습니다.");
        if (IsProtectedDeletionTarget(portalFull))
            throw new IOException("보호된 시스템 폴더에는 파일을 복사하거나 이동할 수 없습니다.");
        if (sourceFull.Equals(portalFull, StringComparison.OrdinalIgnoreCase) ||
            sourceFull.StartsWith(portalFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("포털 자체 또는 포털 내부 항목을 다시 가져올 수 없습니다.");
        var name = Path.GetFileName(sourceFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destination = Path.GetFullPath(Path.Combine(portalFull, name));
        if (Directory.Exists(sourceFull) &&
            (destination.Equals(sourceFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
             destination.StartsWith(sourceFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("원본 폴더를 자기 자신이나 그 하위 폴더로 복사·이동할 수 없습니다.");
        if (IsProtectedDeletionTarget(destination))
            throw new IOException("보호된 시스템 경로로 파일을 복사하거나 이동할 수 없습니다.");
        return destination;
    }

    private static bool IsSameVolume(string first, string second)
        => string.Equals(Path.GetPathRoot(Path.GetFullPath(first)), Path.GetPathRoot(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Directory.Move cannot cross drives. Copy completely first (staged, see CopyDirectory),
    /// then send the original to the Recycle Bin. The original is never permanently deleted;
    /// if recycling fails both copies remain and the user is told.
    /// </summary>
    private static void MoveDirectoryAcrossVolumes(string source, string destination)
    {
        CopyDirectory(source, destination);
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(source,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new IOException($"'{Path.GetFileName(destination)}' 폴더는 복사했지만 다른 드라이브의 원본을 휴지통으로 옮기지 못해 원본이 그대로 남아 있습니다. ({ex.Message})", ex);
        }
    }

    /// <summary>
    /// Stage in the same destination parent, then rename only on complete success.
    /// On copy failure the original is untouched and the final destination is absent.
    /// Never delete a pre-existing destination or a path chosen by the user.
    /// </summary>
    private static void CopyDirectory(string source, string destination)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new IOException("복사 대상 폴더를 확인할 수 없습니다.");
        var staging = Path.Combine(parent, ".CyanDeck-partial-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectoryTree(source, staging);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("복사 도중 같은 이름의 대상이 생성되어 작업을 중단했습니다.");
            Directory.Move(staging, destination);
        }
        catch
        {
            // The staging path was exclusively generated for this operation.
            // Never follow a junction while cleaning a partial copy.
            if (Directory.Exists(staging))
            {
                try
                {
                    EnsureNoReparseInTree(staging);
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception cleanupError)
                {
                    throw new IOException($"복사가 실패했으며 임시 폴더 정리도 실패했습니다. 임시 경로: {staging}", cleanupError);
                }
            }
            throw;
        }
    }

    private static void CopyDirectoryTree(string source, string destination)
    {
        var info = new DirectoryInfo(source);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reparse-point folders are not copied recursively.");
        Directory.CreateDirectory(destination);
        foreach (var file in info.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse-point files are not copied recursively.");
            file.CopyTo(Path.Combine(destination, file.Name), false);
        }
        foreach (var dir in info.EnumerateDirectories())
            CopyDirectoryTree(dir.FullName, Path.Combine(destination, dir.Name));
    }

    private static void EnsureNoReparseInTree(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("임시 폴더에 재분석 지점이 있어 자동 정리를 중단합니다.");
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("임시 폴더에 재분석 지점이 있어 자동 정리를 중단합니다.");
                if ((attributes & FileAttributes.Directory) != 0) queue.Enqueue(child);
            }
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TabDragFormat)) e.Effects = DragDropEffects.Move;
        else e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem is not ItemTile tile) return;
        // Same open path as Enter and the menus (portal folders navigate, opens are counted).
        OpenTileFromUser(tile);
    }

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _listDragStarted = false;
        _pendingSelectionCollapseItem = null;
        if (!_settings.ProtectMultiSelectionDuringDrag || e.ClickCount > 1) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null || !item.IsSelected || ItemsList.SelectedItems.Count <= 1) return;

        // Explorer-style behavior: clicking one member of a multi-selection should not
        // collapse the set before we know whether the user intends to drag it. If no
        // drag starts, collapse to the clicked item on mouse-up instead.
        _pendingSelectionCollapseItem = item;
        Mouse.Capture(ItemsList, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pending = _pendingSelectionCollapseItem;
        _pendingSelectionCollapseItem = null;
        if (ItemsList.IsMouseCaptured) ItemsList.ReleaseMouseCapture();
        if (pending is null || _listDragStarted) return;
        ItemsList.UnselectAll();
        pending.IsSelected = true;
        pending.Focus();
        e.Handled = true;
    }

    private void ItemsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null || item.DataContext is not ItemTile tile) return;
        if (!item.IsSelected)
        {
            ItemsList.UnselectAll();
            item.IsSelected = true;
        }
        var selected = GetSelectedTiles();
        if (selected.Count > 1) ShowBatchContextMenu(selected, item, e);
        else ShowItemContextMenu(tile, item, e.GetPosition(this), e);
    }

    private void ShowBatchContextMenu(IReadOnlyList<ItemTile> tiles, FrameworkElement target, MouseButtonEventArgs e)
    {
        var existing = tiles.Where(t => File.Exists(t.Path) || Directory.Exists(t.Path)).ToList();
        if (existing.Count == 0) return;

        var localPoint = e.GetPosition(this);
        var screen = PointToScreen(localPoint);
        var extras = new List<ShellContextMenuService.ExtraCommand>
        {
            new("경로 복사", () => Clipboard.SetText(string.Join(Environment.NewLine, existing.Select(t => t.Path))))
        };
        if (SelectedTab?.Kind == DeckTabKind.Collection)
        {
            // Same command as the CyanDeck-only menu, which the Windows menu replaces here.
            extras.Add(new("첫 항목을 파일 탐색기에서 표시", () => Reveal(existing[0].Path)));
            extras.Add(new("선택 항목을 바탕화면에 단독 아이콘으로 빼기", () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                ItemsDetachToDesktopRequested?.Invoke(this, tab.Id, existing.Select(t => t.Source.Id).ToArray(), screen);
            }));
            extras.Add(new("선택 항목을 그룹에서만 제거 (원본 유지)", () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                var ids = existing.Select(t => t.Source.Id).ToHashSet();
                tab.Items.RemoveAll(i => ids.Contains(i.Id));
                ClearFreeSelection();
                RefreshItems(IconMovementAnimationContext.ItemSetChanged);
                ModelChanged?.Invoke();
            }));
        }

        // open/rename/group delete are routed to CyanDeck's own commands (see TryHandleNativeShellVerb).
        Func<string, bool>? batchVerbInterceptor = verb => TryHandleNativeShellVerb(verb, existing);
        // Collection tiles are REFERENCES to arbitrary disk paths (including C:\ and user folders).
        // Shell delete/shift-delete verbs would operate on the ORIGINAL filesystem target,
        // potentially permanently. Native Shell verbs are exposed for a collection only when
        // every selected item is a real Desktop child (CanShowNativeShellMenu), as for loose icons.
        if (CanShowNativeShellMenu(existing.Select(t => t.Path)) &&
            existing.All(t => !IsProtectedDeletionTarget(t.Path)) &&
            ShellContextMenuService.TryShow(this, existing.Select(t => t.Path).ToArray(), screen, extras, batchVerbInterceptor))
        {
            e.Handled = true;
            SchedulePostShellMenuRefresh(SelectedTab, existing);
            return;
        }

        // Mixed-parent collections cannot be represented by one Windows Shell context
        // menu. Fall back to CyanDeck's batch commands in that case.
        var menu = CreateThemedContextMenu();
        menu.Items.Add(new MenuItem { Header = $"{existing.Count}개 선택됨", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("선택 항목 열기", () => OpenTilesFromUser(existing)));
        menu.Items.Add(MakeItem("첫 항목을 파일 탐색기에서 표시", () => Reveal(existing[0].Path)));
        menu.Items.Add(MakeItem("경로 복사", () => Clipboard.SetText(string.Join(Environment.NewLine, existing.Select(t => t.Path)))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem(SelectedTab?.Kind == DeckTabKind.Collection &&
            existing.All(t => DesktopRecycleService.IsActualDesktopItem(t.Path))
                ? "선택 항목 삭제… (휴지통 또는 그룹에서만 제거)"
                : SelectedTab?.Kind == DeckTabKind.Collection
                    ? "선택 항목을 그룹에서만 제거 (원본 유지)" : "선택 항목을 휴지통으로 이동",
            () => DeleteItems(existing)));
        if (SelectedTab?.Kind == DeckTabKind.Collection &&
            existing.All(t => DesktopRecycleService.IsActualDesktopItem(t.Path)))
            menu.Items.Add(MakeDangerItem("바탕화면 원본을 휴지통으로 이동…", "Ctrl+Delete",
                () => RecycleActualDesktopCollectionItems(existing)));
        if (SelectedTab?.Kind == DeckTabKind.Collection)
        {
            menu.Items.Add(MakeItem("선택 항목을 바탕화면에 단독 아이콘으로 빼기", () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                ItemsDetachToDesktopRequested?.Invoke(this, tab.Id, existing.Select(t => t.Source.Id).ToArray(), screen);
            }));
            menu.Items.Add(MakeItem("선택 항목을 그룹에서 제거", () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                var ids = existing.Select(t => t.Source.Id).ToHashSet();
                tab.Items.RemoveAll(i => ids.Contains(i.Id));
                ClearFreeSelection();
                RefreshItems(IconMovementAnimationContext.ItemSetChanged);
                ModelChanged?.Invoke();
            }));
        }
        target.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ShowItemContextMenu(ItemTile tile, FrameworkElement target, Point localPoint, MouseButtonEventArgs e)
    {
        if (DesktopSystemItems.IsSupportedVirtualPath(tile.Path))
        {
            var systemMenu = CreateThemedContextMenu();
            systemMenu.Items.Add(MakeItem("열기", () => OpenItem(tile)));
            if (SelectedTab?.Kind == DeckTabKind.Collection)
                systemMenu.Items.Add(MakeItem("그룹에서 제거", () =>
                {
                    var tab = SelectedTab;
                    if (tab is null) return;
                    tab.Items.RemoveAll(i => i.Id == tile.Source.Id);
                    RefreshItems(IconMovementAnimationContext.ItemSetChanged);
                    ModelChanged?.Invoke();
                }));
            target.ContextMenu = systemMenu;
            systemMenu.IsOpen = true;
            e.Handled = true;
            return;
        }
        var screen = PointToScreen(localPoint);
        var extras = new List<ShellContextMenuService.ExtraCommand>
        {
            new("경로 복사", () => Clipboard.SetText(tile.Path))
        };
        if (SelectedTab?.Kind == DeckTabKind.Collection)
        {
            // Same command as the CyanDeck-only menu, which the Windows menu replaces here.
            extras.Add(new("파일 탐색기에서 표시", () => Reveal(tile.Path)));
            extras.Add(new("바탕화면에 단독 아이콘으로 빼기", () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                ItemsDetachToDesktopRequested?.Invoke(this, tab.Id, [tile.Source.Id], screen);
            }));
            extras.Add(new("그룹에서만 제거 (원본 유지)", () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                tab.Items.RemoveAll(i => i.Id == tile.Source.Id || string.Equals(i.Path, tile.Path, StringComparison.OrdinalIgnoreCase));
                RefreshItems(IconMovementAnimationContext.ItemSetChanged);
                ModelChanged?.Invoke();
            }));
        }

        // open/rename/group delete are routed to CyanDeck's own commands (see TryHandleNativeShellVerb).
        Func<string, bool>? verbInterceptor = verb => TryHandleNativeShellVerb(verb, [tile]);
        // Never hand an external Collection reference to a native Shell context menu: a Shell
        // extension may offer irreversible delete on the underlying source directory. Real
        // Desktop children get the Windows menu, as standalone desktop icons do.
        if (CanShowNativeShellMenu([tile.Path]) &&
            !IsProtectedDeletionTarget(tile.Path) &&
            ShellContextMenuService.TryShow(this, tile.Path, screen, extras, verbInterceptor))
        {
            e.Handled = true;
            SchedulePostShellMenuRefresh(SelectedTab, [tile]);
            return;
        }

        var menu = CreateThemedContextMenu();
        menu.Items.Add(MakeItem("열기", () => OpenItem(tile)));
        menu.Items.Add(MakeItem("파일 탐색기에서 표시", () => Reveal(tile.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("이름 바꾸기…", () => RenameItem(tile)));
        menu.Items.Add(MakeItem(SelectedTab?.Kind == DeckTabKind.Collection &&
            DesktopRecycleService.IsActualDesktopItem(tile.Path)
                ? "삭제… (휴지통 또는 그룹에서만 제거)"
                : SelectedTab?.Kind == DeckTabKind.Collection
                    ? "그룹에서만 제거 (원본 유지)" : "휴지통으로 이동", () => DeleteItem(tile)));
        if (SelectedTab?.Kind == DeckTabKind.Collection && DesktopRecycleService.IsActualDesktopItem(tile.Path))
            menu.Items.Add(MakeDangerItem("바탕화면 원본을 휴지통으로 이동…", "Ctrl+Delete",
                () => RecycleActualDesktopCollectionItems([tile])));
        menu.Items.Add(MakeItem("속성", () => ShowProperties(tile.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("경로 복사", () => Clipboard.SetText(tile.Path)));
        if (SelectedTab?.Kind == DeckTabKind.Collection)
        {
            menu.Items.Add(MakeItem("바탕화면에 단독 아이콘으로 빼기", () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                ItemsDetachToDesktopRequested?.Invoke(this, tab.Id, [tile.Source.Id], screen);
            }));
            menu.Items.Add(MakeItem("그룹에서 제거", () =>
            {
                var tab = SelectedTab;
                if (tab is null) return;
                tab.Items.RemoveAll(i => i.Id == tile.Source.Id || string.Equals(i.Path, tile.Path, StringComparison.OrdinalIgnoreCase));
                RefreshItems(IconMovementAnimationContext.ItemSetChanged);
                ModelChanged?.Invoke();
            }));
        }
        target.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ItemsList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var selected = ItemsList.SelectedItems.Cast<ItemTile>().Select(i => i.Path).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (selected.Length == 0) return;
        _listDragStarted = true;
        _pendingSelectionCollapseItem = null;
        if (ItemsList.IsMouseCaptured) ItemsList.ReleaseMouseCapture();
        var data = new DataObject(DataFormats.FileDrop, selected);
        try { DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy | DragDropEffects.Link); }
        finally { _listDragStarted = false; }
    }

    private void RenameItem(ItemTile tile)
    {
        // A Collection tile is a reference to an external object. Change only its
        // CyanDeck label; never rename the actual file or directory on disk.
        if (SelectedTab?.Kind == DeckTabKind.Collection)
        {
            var newLabel = TextPrompt.Show(this, "그룹 항목 이름 변경", "그룹 안에 표시할 이름", tile.Source.DisplayNameOverride ?? tile.Name);
            if (newLabel is null) return;
            tile.Source.DisplayNameOverride = newLabel;
            RefreshItems(IconMovementAnimationContext.ItemSetChanged);
            ModelChanged?.Invoke();
            return;
        }
        if (SelectedTab?.Kind != DeckTabKind.FolderPortal) return;
        if (IsProtectedDeletionTarget(tile.Path))
        {
            System.Windows.MessageBox.Show("드라이브 루트와 주요 Windows/사용자 폴더 이름은 여기서 변경할 수 없습니다.",
                "CyanDeck — 원본 보호", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!File.Exists(tile.Path) && !Directory.Exists(tile.Path)) return;
        var oldName = Path.GetFileName(tile.Path.TrimEnd(Path.DirectorySeparatorChar));
        var newName = TextPrompt.Show(this, "이름 바꾸기", "새 이름", oldName);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        // Explain names Windows rejects ("." / "..", trailing dot or space, CON, ...) instead of
        // letting them fail later with a misleading "already exists" message.
        if (WindowsFileNamePolicy.GetProblem(newName) is { } nameProblem)
        {
            System.Windows.MessageBox.Show(nameProblem, "CyanDeck — 이름 바꾸기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var parent = Path.GetDirectoryName(tile.Path.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent)) return;
            var destination = Path.Combine(parent, newName);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("같은 이름의 항목이 이미 있습니다.");
            if (File.Exists(tile.Path)) File.Move(tile.Path, destination);
            else Directory.Move(tile.Path, destination);
            if (SelectedTab?.Kind == DeckTabKind.Collection) tile.Source.Path = destination;
            RefreshItems(IconMovementAnimationContext.ItemSetChanged);
            ModelChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteItem(ItemTile tile) => DeleteItems([tile]);

    /// <summary>
    /// Only actual files directly on the user's or Public Desktop can be recycled
    /// from a Collection. The Delete choice dialog itself is explicit confirmation;
    /// Ctrl+Delete and the separate context action still require Yes/No confirmation.
    /// </summary>
    private void RecycleActualDesktopCollectionItems(IReadOnlyList<ItemTile> tiles, bool confirmedByChoiceDialog = false)
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection || tiles.Count == 0) return;
        var paths = tiles.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Any(p => !DesktopRecycleService.IsActualDesktopItem(p)))
        {
            System.Windows.MessageBox.Show(this,
                "바탕화면에 직접 저장된 파일·폴더만 이 기능으로 휴지통에 보낼 수 있습니다.\n외부 폴더와 드라이브는 그룹에서만 제거할 수 있습니다.",
                "CyanDeck — 원본 보호", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = paths.Length == 1 ? $"'{Path.GetFileName(paths[0])}'" : $"선택한 {paths.Length}개 항목";
        if (!confirmedByChoiceDialog && System.Windows.MessageBox.Show(this,
            $"{name}을(를) 실제 바탕화면에서 휴지통으로 이동하시겠습니까?\n\n" +
            "이는 그룹에서만 제거하는 동작이 아닙니다. 바탕화면 원본 파일·폴더가 휴지통으로 이동합니다.\n" +
            "취소하려면 '아니요'를 선택하세요.",
            "CyanDeck — 실제 파일 삭제 확인", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var result = DesktopRecycleService.RecycleConfirmed(paths, warning =>
            System.Windows.MessageBox.Show(this, warning,
                "CyanDeck — 영구 삭제 위험 추가 확인", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        if (result.Recycled.Count != 0)
        {
            var moved = result.Recycled.ToHashSet(StringComparer.OrdinalIgnoreCase);
            tab.Items.RemoveAll(i => moved.Contains(i.Path));
            ClearFreeSelection();
            RefreshItems(IconMovementAnimationContext.ItemSetChanged);
            ModelChanged?.Invoke();
        }
        if (result.Errors.Count != 0)
            System.Windows.MessageBox.Show(this,
                "일부 항목을 휴지통으로 이동하지 못했습니다. 실패 항목의 원본은 건드리지 않았습니다.\n\n" +
                string.Join(Environment.NewLine, result.Errors.Take(8)),
                "CyanDeck — 휴지통 이동 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Removes virtual Collection references only; never touches the filesystem.
    /// C:\, Downloads and mixed selections stay on this path. A dedicated dialog
    /// button is already confirmation when confirmedByChoiceDialog is true.
    /// </summary>
    private void RemoveCollectionReferences(IReadOnlyList<ItemTile> tiles, bool confirmedByChoiceDialog = false)
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection || tiles.Count == 0) return;
        var ids = tiles.Select(t => t.Source.Id).ToHashSet();
        var found = tab.Items.Count(i => ids.Contains(i.Id));
        if (found == 0) return;
        var message = found == 1
            ? "이 항목을 CyanDeck 그룹에서만 제거하시겠습니까?\n\n실제 원본 파일·폴더는 삭제하거나 이동하지 않습니다."
            : $"선택한 {found}개 항목을 CyanDeck 그룹에서만 제거하시겠습니까?\n\n실제 원본 파일·폴더는 삭제하거나 이동하지 않습니다.";
        if (!confirmedByChoiceDialog && System.Windows.MessageBox.Show(this, message, "CyanDeck — 원본 보호",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        tab.Items.RemoveAll(i => ids.Contains(i.Id));
        ClearFreeSelection();
        RefreshItems(IconMovementAnimationContext.ItemSetChanged);
        ModelChanged?.Invoke();
    }

    private void DeleteItems(IReadOnlyList<ItemTile> tiles)
    {
        // A Collection is virtual. For a selection containing only genuine Desktop
        // children, Delete/Shift+Delete lets the user explicitly choose to recycle
        // the source OR merely remove its group reference. Mixed selections and
        // external paths (including drive roots and Downloads) are always reference-only.
        if (SelectedTab?.Kind == DeckTabKind.Collection)
        {
            if (tiles.Count > 0 && tiles.All(t => DesktopRecycleService.IsActualDesktopItem(t.Path)))
            {
                switch (CollectionDesktopDeleteDialog.Show(this, tiles.Count))
                {
                    case CollectionDesktopDeleteChoice.Recycle:
                        RecycleActualDesktopCollectionItems(tiles, confirmedByChoiceDialog: true);
                        break;
                    case CollectionDesktopDeleteChoice.RemoveFromGroup:
                        RemoveCollectionReferences(tiles, confirmedByChoiceDialog: true);
                        break;
                    // Cancel, Escape or closing the dialog must never modify files.
                }
            }
            else
            {
                RemoveCollectionReferences(tiles);
            }
            return;
        }
        if (SelectedTab?.Kind != DeckTabKind.FolderPortal) return;
        var existing = tiles.Where(t => File.Exists(t.Path) || Directory.Exists(t.Path)).DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return;
        if (existing.Any(t => IsProtectedDeletionTarget(t.Path)))
        {
            System.Windows.MessageBox.Show("드라이브 루트 또는 주요 Windows/사용자 폴더는 CyanDeck에서 삭제할 수 없습니다.\n원본 폴더를 보호하기 위해 이 작업을 취소했습니다.",
                "CyanDeck — 원본 보호", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var label = existing.Count == 1 ? $"'{existing[0].Name}'" : $"선택한 {existing.Count}개 항목";
        var result = System.Windows.MessageBox.Show($"{label}을(를) 휴지통으로 이동하시겠습니까?", "CyanDeck", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        var largeThreshold = Math.Clamp(_settings.LargeBatchOperationThreshold, 5, 500);
        if (_settings.ConfirmLargeBatchOperations && existing.Count >= largeThreshold)
        {
            var second = System.Windows.MessageBox.Show(
                $"대량 작업입니다({existing.Count}개 항목). 선택한 항목을 모두 휴지통으로 이동하시겠습니까?",
                "CyanDeck — 대량 작업 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (second != MessageBoxResult.Yes) return;
        }
        // Probe the entire selection, including every accessible descendant of
        // a folder. The Windows Recycle Bin may silently bypass its capacity.
        var recycleRisk = RecycleBinPreflight.Assess(existing.Select(tile => tile.Path));
        if (recycleRisk.NeedsAdditionalConfirmation &&
            System.Windows.MessageBox.Show(this, recycleRisk.Warning,
                "CyanDeck — 영구 삭제 위험 추가 확인", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var errors = new List<string>();
        var deletedIds = new HashSet<Guid>();
        foreach (var tile in existing)
        {
            try
            {
                if (IsProtectedDeletionTarget(tile.Path))
                    throw new IOException("보호 경로로 변경되어 휴지통 이동을 중단했습니다.");
                if (File.Exists(tile.Path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(tile.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                else if (Directory.Exists(tile.Path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(tile.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                else
                    throw new FileNotFoundException("삭제 대상이 존재하지 않습니다.", tile.Path);
                if (File.Exists(tile.Path) || Directory.Exists(tile.Path))
                    throw new IOException("휴지통 이동이 확인되지 않아 원본 참조를 유지합니다.");
                deletedIds.Add(tile.Source.Id);
            }
            catch (Exception ex) { errors.Add($"{tile.Name}: {ex.Message}"); }
        }
        if (SelectedTab?.Kind == DeckTabKind.Collection) SelectedTab.Items.RemoveAll(i => deletedIds.Contains(i.Id));
        ClearFreeSelection();
        RefreshItems(IconMovementAnimationContext.ItemSetChanged);
        ModelChanged?.Invoke();
        if (errors.Count > 0)
            System.Windows.MessageBox.Show(string.Join(Environment.NewLine, errors.Take(8)), "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool IsProtectedDeletionTarget(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
            var criticalPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)) ?? "",
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };
            // OS/program files and AppData must be protected INCLUDING descendants;
            // user documents, downloads and desktop are protected at their root only.
            // Comparison includes a directory separator to avoid matching e.g. WindowsOld.
            var protectedTrees = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            if (protectedTrees.Any(folder => !string.IsNullOrWhiteSpace(folder) &&
                (full.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                return true;
            // Reject junctions/symlinks at any existing ancestor, not just the leaf.
            var current = full;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            return criticalPaths.Concat(DesktopDirectoryService.GetRoots()).Any(folder => !string.IsNullOrWhiteSpace(folder) &&
                string.Equals(full, Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
        }
        catch { return true; }
    }

    private void OpenItem(ItemTile tile)
    {
        try
        {
            if (DesktopSystemItems.IsSupportedVirtualPath(tile.Path))
                DesktopSystemItems.OpenVirtualItem(tile.Path);
            else
                ShortcutLaunchService.Open(tile.Path);
            if (SelectedTab?.Kind == DeckTabKind.Collection)
            {
                if (tile.Source.OpenCount < long.MaxValue) tile.Source.OpenCount++;
                ModelChanged?.Invoke();
                if (SelectedTab.Sort == DeckSortMode.OpenCount) RefreshItems(IconMovementAnimationContext.SortOrArrange);
            }
        }
        catch (ShortcutLaunchException ex)
        {
            ShortcutLaunchService.ReportFailure(ex);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void ShowProperties(string path)
    {
        if (ShellPropertiesLauncher.TryShow(path, out var error)) return;
        System.Windows.MessageBox.Show($"속성 창을 열 수 없습니다.\n\n{path}\n{error}", "CyanDeck",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void Open(string path)
    {
        try { ShortcutLaunchService.Open(path); }
        catch (ShortcutLaunchException ex) { ShortcutLaunchService.ReportFailure(ex); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public void PreviewPagePull(int direction, double progress)
    {
        if (!IsLoaded || direction == 0) return;
        progress = Math.Clamp(progress, 0d, 1d);
        var offset = GetPageAnimationOffset() * 0.14 * progress * -Math.Sign(direction);
        _applyingLayout = true;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(OpacityProperty, null);
        Left = Model.X + offset;
        Opacity = 1d - (0.08 * progress);
    }

    public void CancelPagePullPreview()
    {
        if (!IsLoaded) return;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(OpacityProperty, null);
        Left = Model.X;
        Opacity = 1;
        _applyingLayout = false;
    }

    internal double GetPageTransitionSurfaceWidthDip() => GetPageAnimationOffset();

    internal void AnimatePageIn(DesktopPageTransitionPlan plan)
    {
        if (!IsLoaded || !plan.Animate)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(OpacityProperty, null);
            Left = Model.X;
            Opacity = 1;
            _applyingLayout = false;
            if (!IsVisible) Show();
            return;
        }

        _applyingLayout = true;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(OpacityProperty, null);
        Left = Model.X;
        Opacity = 1;
        if (!IsVisible) Show();

        var move = WpfDesktopPageTransitionAnimationFactory.CreateIncomingSlide(plan);
        move.From = Model.X + plan.IncomingFromX;
        move.To = Model.X + plan.IncomingToX;
        var fade = WpfDesktopPageTransitionAnimationFactory.CreateIncomingOpacity(plan);
        move.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(OpacityProperty, null);
            Left = Model.X;
            Opacity = 1;
            _applyingLayout = false;
        };
        BeginAnimation(LeftProperty, move, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    internal void AnimatePageOut(DesktopPageTransitionPlan plan, Action completed)
    {
        if (!IsLoaded || !plan.Animate)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(OpacityProperty, null);
            Left = Model.X;
            Opacity = 1;
            _applyingLayout = false;
            completed();
            return;
        }

        _applyingLayout = true;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(OpacityProperty, null);
        Left = Model.X;
        Opacity = 1;
        var move = WpfDesktopPageTransitionAnimationFactory.CreateOutgoingSlide(plan);
        move.From = Model.X + plan.OutgoingFromX;
        move.To = Model.X + plan.OutgoingToX;
        move.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(OpacityProperty, null);
            Left = Model.X;
            Opacity = 1;
            _applyingLayout = false;
            completed();
        };
        BeginAnimation(LeftProperty, move, HandoffBehavior.SnapshotAndReplace);
    }

    internal void AnimatePageCancel(DesktopPageTransitionPlan plan)
    {
        if (!IsLoaded) return;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(OpacityProperty, null);

        if (!plan.Animate)
        {
            Left = Model.X;
            Opacity = 1;
            _applyingLayout = false;
            return;
        }

        _applyingLayout = true;
        Left = Model.X;
        Opacity = 1;
        var move = WpfDesktopPageTransitionAnimationFactory.CreateOutgoingSlide(plan);
        move.From = Model.X + plan.OutgoingFromX;
        move.To = Model.X + plan.OutgoingToX;
        move.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = Model.X;
            Opacity = 1;
            _applyingLayout = false;
        };
        BeginAnimation(LeftProperty, move, HandoffBehavior.SnapshotAndReplace);
    }

    private double GetPageAnimationOffset()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var center = PointToScreen(new Point(Math.Max(1, ActualWidth / 2), Math.Max(1, ActualHeight / 2)));
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
            return Math.Max(420, screen.WorkingArea.Width / Math.Max(.5, dpi.DpiScaleX));
        }
        catch { return Math.Max(900, SystemParameters.WorkArea.Width); }
    }

    public int GetPageEdgeDirection()
    {
        if (!IsLoaded) return 0;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
            var wa = screen.WorkingArea;
            var left = wa.Left / dpi.DpiScaleX;
            var right = wa.Right / dpi.DpiScaleX;
            const double edge = 3;
            if (Left <= left + edge) return -1;
            if (Left + ActualWidth >= right - edge) return 1;
        }
        catch { }
        return 0;
    }

    public void MoveFromPageEdge(int direction)
    {
        if (!IsLoaded || direction == 0) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y));
            var wa = screen.WorkingArea;
            var left = wa.Left / dpi.DpiScaleX;
            var right = wa.Right / dpi.DpiScaleX;
            Left = direction > 0 ? left + 24 : Math.Max(left + 24, right - ActualWidth - 24);
            Model.X = Left;
            Model.Y = Top;
        }
        catch { }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var step = e.Delta > 0 ? 4 : -4;
        SetIconSize(Model.IconSize + step);
        e.Handled = true;
    }

    private void UpdateHeaderControls(bool pointerInside)
    {
        if (HeaderButtonsPanel is null) return;
        if (!_settings.ShowControlsInGroupHeaders)
        {
            HeaderButtonsPanel.Visibility = Visibility.Collapsed;
            HeaderButtonsPanel.IsHitTestVisible = false;
            return;
        }
        HeaderButtonsPanel.IsHitTestVisible = true;
        HeaderButtonsPanel.Visibility = !_settings.ShowTitlebarControlsOnMouseover || pointerInside || _peekMode
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private readonly record struct IconMovementBatch(Guid TabId, long Token);

    private string GetIconMovementSurfaceId(DeckTab tab)
        => $"fence:{Model.Id:D}:tab:{tab.Id:D}";

    private IconMovementAnimationOptions GetIconMovementAnimationOptions()
        => new IconMovementAnimationOptions(
            Enabled: _settings.AnimateIconsWhenMoved,
            DurationMs: _settings.IconMovementAnimationDurationMs,
            MaxAnimatedItems: _settings.IconMovementAnimationMaxItems,
            MinimumDistanceDip: IconMovementAnimationOptions.Default.MinimumDistanceDip,
            MaximumDistanceDip: IconMovementAnimationOptions.Default.MaximumDistanceDip,
            RespectSystemAnimationSetting: true).Normalize();

    private IconMovementBatch? BeginIconMovementBatch(
        DeckTab tab,
        IconMovementAnimationContext context,
        IReadOnlyCollection<string>? excludedItemIds = null)
    {
        if (!_settings.AnimateIconsWhenMoved || IconMovementAnimationPlanner.IsSuppressedContext(context))
        {
            CancelIconMovementAnimationState(resetTransforms: true);
            return null;
        }

        var token = _iconMovementAnimationCoordinator.Begin(
            CaptureIconMovementAnchors(tab),
            context,
            excludedItemIds);
        return new IconMovementBatch(tab.Id, token);
    }

    private void CompleteIconMovementBatchAfterLayout(IconMovementBatch? batch)
    {
        if (batch is not { } pending) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var tab = SelectedTab;
            if (tab is null || tab.Id != pending.TabId || !IsLoaded)
            {
                _iconMovementAnimationCoordinator.Cancel();
                return;
            }

            var plan = _iconMovementAnimationCoordinator.Complete(
                pending.Token,
                CaptureIconMovementAnchors(tab),
                GetIconMovementAnimationOptions(),
                WpfIconMovementAnimator.ReadSystemAnimationPreference());
        }));
    }

    private IReadOnlyList<IconMovementAnchor> CaptureIconMovementAnchors(DeckTab tab)
    {
        var surfaceId = GetIconMovementSurfaceId(tab);
        var anchors = new List<IconMovementAnchor>();
        var useFree = tab.Kind == DeckTabKind.Collection && Model.IconLayoutMode == DeckIconLayoutMode.Free && tab.ViewMode == DeckItemViewMode.Grid;
        if (useFree)
        {
            foreach (var border in _freeBorders.Values)
            {
                if (border.Tag is not ItemTile tile) continue;
                AddIconMovementAnchor(anchors, surfaceId, tile.StableItemId, border, FreeCanvas);
            }
            return anchors;
        }

        foreach (var tile in ItemsList.Items.OfType<ItemTile>())
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(tile) is FrameworkElement element)
                AddIconMovementAnchor(anchors, surfaceId, tile.StableItemId, element, ItemsList);
        }
        return anchors;
    }

    private static void AddIconMovementAnchor(
        ICollection<IconMovementAnchor> anchors,
        string surfaceId,
        string itemId,
        FrameworkElement element,
        UIElement relativeTo)
    {
        try
        {
            if (!element.IsLoaded || element.Visibility != Visibility.Visible) return;
            var point = element.TranslatePoint(new Point(0, 0), relativeTo);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
            anchors.Add(new IconMovementAnchor(surfaceId, itemId, point.X, point.Y));
        }
        catch { }
    }

    private WpfIconMovementAnimationTarget? ResolveIconMovementTarget(string surfaceId, string itemId)
    {
        var tab = SelectedTab;
        if (tab is null || !string.Equals(surfaceId, GetIconMovementSurfaceId(tab), StringComparison.Ordinal)) return null;
        var useFree = tab.Kind == DeckTabKind.Collection && Model.IconLayoutMode == DeckIconLayoutMode.Free && tab.ViewMode == DeckItemViewMode.Grid;
        if (useFree)
        {
            foreach (var border in _freeBorders.Values)
            {
                if (border.Tag is not ItemTile tile || !string.Equals(tile.StableItemId, itemId, StringComparison.Ordinal)) continue;
                var transform = EnsureMovementTransform(border);
                return new WpfIconMovementAnimationTarget(border, transform);
            }
            return null;
        }

        foreach (var tile in ItemsList.Items.OfType<ItemTile>())
        {
            if (!string.Equals(tile.StableItemId, itemId, StringComparison.Ordinal)) continue;
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(tile) is not FrameworkElement element) return null;
            var transform = EnsureMovementTransform(element);
            return new WpfIconMovementAnimationTarget(element, transform);
        }
        return null;
    }

    private static TranslateTransform EnsureMovementTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform transform) return transform;
        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private void ItemContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.AnimateIconsWhenMoved && sender is FrameworkElement element) EnsureMovementTransform(element);
    }

    private void CancelIconMovementAnimationState(bool resetTransforms)
    {
        _iconMovementAnimationCoordinator.Cancel();
        _resizeIconMovementToken = null;
        _resizeIconMovementTabId = null;
        if (!resetTransforms) return;

        var tab = SelectedTab;
        if (tab is null) return;
        var useFree = tab.Kind == DeckTabKind.Collection && Model.IconLayoutMode == DeckIconLayoutMode.Free && tab.ViewMode == DeckItemViewMode.Grid;
        if (useFree)
        {
            foreach (var border in _freeBorders.Values)
            {
                if (border.RenderTransform is TranslateTransform transform)
                    WpfIconMovementAnimator.Cancel(new WpfIconMovementAnimationTarget(border, transform));
            }
            return;
        }
        foreach (var tile in ItemsList.Items.OfType<ItemTile>())
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(tile) is FrameworkElement element
                && element.RenderTransform is TranslateTransform transform)
                WpfIconMovementAnimator.Cancel(new WpfIconMovementAnimationTarget(element, transform));
        }
    }

    private static string NormalizePortalMovementItemId(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
        catch { return (path ?? string.Empty).Trim().ToUpperInvariant(); }
    }

    private void ApplyItemScrollbars(bool pointerInside)
    {
        var tab = SelectedTab;
        var hide = _settings.HideScrollbarsWhenInactive && !pointerInside;
        var activeVisibility = hide ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        if (tab is not null)
        {
            var horizontalList = tab.ViewMode == DeckItemViewMode.List && tab.ListOrientation == DeckListOrientation.Horizontal;
            ScrollViewer.SetHorizontalScrollBarVisibility(ItemsList, horizontalList ? activeVisibility : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(ItemsList, horizontalList ? ScrollBarVisibility.Disabled : activeVisibility);
        }
        FreeScroll.HorizontalScrollBarVisibility = activeVisibility;
        FreeScroll.VerticalScrollBarVisibility = activeVisibility;
    }

    private static int ComputeTileSignature(IEnumerable<ItemTile> tiles)
    {
        var hash = 17;
        foreach (var tile in tiles)
            hash = HashCode.Combine(hash, StringComparer.OrdinalIgnoreCase.GetHashCode(tile.Path));
        return hash;
    }

    private void AnimateItemSurfaceIfChanged(FrameworkElement surface, ref int? previousSignature, int signature)
    {
        var changed = previousSignature is null || previousSignature.Value != signature;
        previousSignature = signature;
        if (!changed || !_settings.AnimateItemChanges || !IsLoaded || SearchBox.IsKeyboardFocusWithin) return;
        var transform = surface.RenderTransform as TranslateTransform ?? new TranslateTransform();
        surface.RenderTransform = transform;
        var duration = TimeSpan.FromMilliseconds(130);
        surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0.82, 1.0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(3.5, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void CancelPortalLoad()
    {
        var cts = _portalLoadCts;
        _portalLoadCts = null;
        _portalLoadingTabId = null;
        _portalLoadingPath = null;
        _portalLoadingVisualKey = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private void CompletePortalLoad(CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_portalLoadCts, cts)) return;
        _portalLoadCts = null;
        cts.Dispose();
    }

    // Presentation only: never persists layout or mutates the user's desktop.
    private void ReconcileInitialHoverPresentation()
    {
        if (!IsLoaded || !IsVisible) return;
        var pointerInside = DesktopPointerHover.IsActuallyOver(this);
        // Let the next real pointer movement run the full enter path.
        _hoverPresentationActive = false;
        ApplyTitlebarMode(pointerInside);
        UpdateHeaderControls(pointerInside);
        ApplyItemScrollbars(pointerInside);
        if (_chameleonActivationBridge is not null) _chameleonActivationBridge.Refresh();
        else ApplyChameleonDecision(ChameleonPresentationDecision.DeferToExistingPolicy);
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Layered HWND attachment can synthesize MouseEnter without pointer motion.
        if (!DesktopPointerHover.IsActuallyOver(this))
        {
            ReconcileInitialHoverPresentation();
            return;
        }
        ApplyPointerEnteredPresentation();
    }

    // After a synthetic MouseEnter is ignored, WPF can still consider the pointer "over" this
    // window and will not raise MouseEnter again when the pointer really arrives. The first real
    // movement inside the window therefore completes the entered presentation (header controls,
    // full colour, scrollbars, roll-up preview).
    private void Fence_HoverMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_hoverPresentationActive || _moving || _movePending) return;
        if (!DesktopPointerHover.IsActuallyOver(this)) return;
        ApplyPointerEnteredPresentation();
    }

    private void ApplyPointerEnteredPresentation()
    {
        _hoverPresentationActive = true;
        VerifyCutClipboardAfterInteraction();
        if (_chameleonActivationBridge is null) Shell.Opacity = 1;
        else _chameleonActivationBridge.Refresh();
        ApplyTitlebarMode(true);
        UpdateHeaderControls(true);
        ApplyItemScrollbars(true);
        if (!_settings.EnableRollup || !Model.RolledUp) return;
        _rollupHoverTimer?.Stop();
        if (Model.RollupActivation == DeckRollupActivation.Hover)
        {
            SetRollupPreview(true);
        }
        else if (Model.RollupActivation == DeckRollupActivation.DelayedHover)
        {
            _rollupHoverTimer ??= new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
            _rollupHoverTimer.Stop();
            _rollupHoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Model.RollupHoverDelayMs, 100, 2000));
            _rollupHoverTimer.Tick -= RollupHoverTimer_Tick;
            _rollupHoverTimer.Tick += RollupHoverTimer_Tick;
            _rollupHoverTimer.Start();
        }
    }

    private void RollupHoverTimer_Tick(object? sender, EventArgs e)
    {
        _rollupHoverTimer?.Stop();
        if (IsMouseOver && Model.RolledUp && Model.RollupActivation == DeckRollupActivation.DelayedHover)
            SetRollupPreview(true);
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverPresentationActive = false;
        _rollupHoverTimer?.Stop();
        ApplyTitlebarMode(false);
        UpdateHeaderControls(false);
        ApplyItemScrollbars(false);
        if (Model.RolledUp) SetRollupPreview(false);
        if (_chameleonActivationBridge is not null)
        {
            _chameleonActivationBridge.Refresh();
            return;
        }
        if (_peekMode || !_settings.EnableChameleon || !Model.AutoFade) return;
        Shell.Opacity = Math.Clamp(_settings.ChameleonIdleOpacity, 0.15, 1);
    }

    private void QueueWallpaperBlurRefresh()
    {
        // Hidden groups do not need a background snapshot. Visibility/appearance refreshes
        // already rebuild their layer when they become visible again.
        if (!IsLoaded || !IsVisible || !_settings.BlurWallpaperBehindGroups) return;
        if (!_wallpaperBlurRefreshTimer.IsEnabled) _wallpaperBlurRefreshTimer.Start();
    }

    private void RefreshWallpaperBlur()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        // Explicit refreshes on move completion and appearance changes supersede a queued tick.
        _wallpaperBlurRefreshTimer.Stop();
        try
        {
            var dpi = VisualTreeHelper.GetDpi(WallpaperBlurBackdrop);
            // The backdrop Canvas is INSIDE the border. Derive its own screen-space corners,
            // not the outer Window origin plus ActualWidth: this avoids border/layout rounding
            // offsets and ensures consistency across per-monitor DPI transitions.
            var canvas = WallpaperBlurBackdrop;
            if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
            var topLeft = canvas.PointToScreen(new Point(0, 0));
            var bottomRight = canvas.PointToScreen(new Point(canvas.ActualWidth, canvas.ActualHeight));
            var left = (int)Math.Round(topLeft.X);
            var top = (int)Math.Round(topLeft.Y);
            var right = (int)Math.Round(bottomRight.X);
            var bottom = (int)Math.Round(bottomRight.Y);
            _wallpaperBlurController.Refresh(new WallpaperBlurPolicyInput(
                _settings.BlurWallpaperBehindGroups,
                SystemParameters.HighContrast,
                Model.RolledUp || !IsVisible,
                new PixelRect(left, top, right, bottom),
                dpi.DpiScaleX,
                dpi.DpiScaleY));
        }
        catch
        {
            // Retain the previous fail-closed behavior during transient WPF/Explorer recovery.
            _wallpaperBlurController.Refresh(new WallpaperBlurPolicyInput(
                false, SystemParameters.HighContrast, true, default, 1, 1));
        }
    }

    private void ApplyChameleonDecision(ChameleonPresentationDecision decision)
    {
        var targetOpacity = decision switch
        {
            ChameleonPresentationDecision.ShowFullColor => 1d,
            ChameleonPresentationDecision.KeepBlended => Math.Clamp(_settings.ChameleonIdleOpacity, 0.15, 1),
            _ => _peekMode || !_settings.EnableChameleon || !Model.AutoFade || DesktopPointerHover.IsActuallyOver(this)
                ? 1d
                : Math.Clamp(_settings.ChameleonIdleOpacity, 0.15, 1),
        };

        // Avoid needlessly invalidating the layered WPF surface when foreground/hover observers
        // report the same presentation state repeatedly. A repeated opacity write is visually
        // pointless and, on transparent top-level windows, can contribute to compositor flashes.
        if (_lastAppliedChameleonOpacity is double last && Math.Abs(last - targetOpacity) < 0.001 &&
            Math.Abs(Shell.Opacity - targetOpacity) < 0.001)
        {
            return;
        }

        _lastAppliedChameleonOpacity = targetOpacity;
        if (Math.Abs(Shell.Opacity - targetOpacity) < 0.001)
        {
            return;
        }

        Shell.Opacity = targetOpacity;
        DiagnosticsLog.Info(
            $"Chameleon presentation changed: fence={Model.Id:N}; opacity={targetOpacity:0.00}; decision={decision}; mouseOver={IsMouseOver}; peek={_peekMode}");
    }

    private static bool IsDesktopOrCyanDeckForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var foreground = hwnd;
        for (var i = 0; i < 8 && hwnd != IntPtr.Zero; i++)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return true;
            hwnd = NativeMethods.GetParent(hwnd);
        }
        return DesktopHost.IsExplorerDesktopWindow(foreground);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T value) return value;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value ?? "")!;
            return color;
        }
        catch { return fallback; }
    }

    private sealed record AppearanceSnapshot(
        double Opacity,
        string Background,
        string Accent,
        double CornerRadius,
        string IconTint,
        double IconTintStrength,
        string TitleFontFamily,
        double TitleFontSize,
        double IconLabelFontSize,
        string TitleForeground,
        DeckTabStyle TabStyle,
        DeckTitlebarMode TitlebarMode,
        bool AutoFade,
        bool MoveTitlebarToBottomNearTop)
    {
        public static AppearanceSnapshot From(DeckFence fence) => new(
            fence.Opacity, fence.Background, fence.Accent, fence.CornerRadius, fence.IconTint, fence.IconTintStrength,
            fence.TitleFontFamily, fence.TitleFontSize, fence.IconLabelFontSize, fence.TitleForeground, fence.TabStyle, fence.TitlebarMode,
            fence.AutoFade, fence.MoveTitlebarToBottomNearTop);

        public void ApplyTo(DeckFence fence)
        {
            fence.Opacity = Opacity;
            fence.Background = Background;
            fence.Accent = Accent;
            fence.CornerRadius = CornerRadius;
            fence.IconTint = IconTint;
            fence.IconTintStrength = IconTintStrength;
            fence.TitleFontFamily = TitleFontFamily;
            fence.TitleFontSize = TitleFontSize;
            fence.IconLabelFontSize = IconLabelFontSize;
            fence.TitleForeground = TitleForeground;
            fence.TabStyle = TabStyle;
            fence.TitlebarMode = TitlebarMode;
            fence.AutoFade = AutoFade;
            fence.MoveTitlebarToBottomNearTop = MoveTitlebarToBottomNearTop;
        }
    }

    public sealed class ItemTile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public double CutOpacity => DeckCutClipboardVisualState.IsCut(Path) ? 0.45 : 1.0;
        internal void UpdateCutOpacity() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CutOpacity)));

        public DeckItem Source { get; }
        public string Path => Source.Path;
        public string Name => Source.DisplayName;
        public ImageSource Icon { get; }
        public DateTime Modified { get; }
        public long Size { get; }
        public double IconDisplaySize { get; }
        public double TileWidth { get; }
        public double TileHeight { get; }
        public double IconRowHeight { get; }
        public DeckIconLabelMode LabelMode { get; }
        public bool IsFolderPortalItem { get; }
        public string StableItemId { get; }
        public bool ShowLabelAlways => LabelMode == DeckIconLabelMode.Always;
        public bool ShowLabelOnHover => LabelMode == DeckIconLabelMode.Mouseover;
        /// <summary>Computed once when the tile is built; bindings, sort and search reuse it (no disk probe).</summary>
        public bool IsDirectory { get; init; }
        /// <summary>File/folder creation time (UTC); used by the Folder Portal "만든 날짜" sort.</summary>
        public DateTime CreatedUtc { get; init; }
        private string? _type;
        public string Type => _type ??= IsDirectory ? "폴더" : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
        public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("g", CultureInfo.CurrentCulture);
        public string SizeText => IsDirectory ? "" : FormatSize(Size);

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            var value = bytes / 1024d;
            if (value < 1024) return $"{value:0.#} KB";
            value /= 1024d;
            if (value < 1024) return $"{value:0.#} MB";
            value /= 1024d;
            return $"{value:0.#} GB";
        }

        public ItemTile(DeckItem source, ImageSource icon, DateTime modified, long size, double iconDisplaySize, double tileWidth, double tileHeight, double iconRowHeight, DeckIconLabelMode labelMode, bool isFolderPortalItem = false)
        {
            Source = source;
            Icon = icon;
            Modified = modified;
            Size = size;
            IconDisplaySize = iconDisplaySize;
            TileWidth = tileWidth;
            TileHeight = tileHeight;
            IconRowHeight = iconRowHeight;
            LabelMode = labelMode;
            IsFolderPortalItem = isFolderPortalItem;
            StableItemId = isFolderPortalItem
                ? NormalizePortalMovementItemId(source.Path)
                : source.Id.ToString("D");
        }
    }
}
