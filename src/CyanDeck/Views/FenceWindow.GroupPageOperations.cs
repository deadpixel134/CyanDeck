using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CyanDeck.Models;
using CyanDeck.Services;
using CyanDeck.Topology;
using Forms = System.Windows.Forms;

namespace CyanDeck.Views;

/// <summary>
/// Group ↔ page operations (send to page, drop-at-edge preview, seam fitting), template
/// entries, the "메뉴만" tab list, Peek exit from the keyboard and revealing an item found by
/// the global item search. Kept apart from rendering and the main menu composition.
/// </summary>
public partial class FenceWindow
{
    // Desktop folders are not part of the in-group search text (see MatchesSearch).
    private static readonly string[] SearchExcludedDesktopRoots = ReadSearchExcludedDesktopRoots();

    private int _pageEdgeHintDirection;
    private long _pageEdgeHintSince;
    private bool _pageEdgeHintVisualActive;
    private DispatcherTimer? _pageEdgeHintTimer;
    private Popup? _pageEdgeHintPopup;
    private TextBlock? _pageEdgeHintText;

    private DispatcherTimer? _revealTimer;
    private string? _revealPath;
    private int _revealTicks;
    private bool _revealFound;
    private bool _locationHighlightActive;
    private DispatcherTimer? _locationHighlightTimer;

    /// <summary>Highest existing desktop page index (for the "다른 페이지로 보내기" list).</summary>
    internal Func<int>? MaxPageIndexProvider { get; set; }
    /// <summary>Page index for "새 페이지로 보내기", or -1 when this group is alone on the last page.</summary>
    internal Func<FenceWindow, int>? NewPageTargetProvider { get; set; }
    /// <summary>-1/+1 while the pointer dragging this group rests on a page-switching screen edge.</summary>
    internal Func<FenceWindow, int>? PageEdgeDropDirectionResolver { get; set; }
    /// <summary>"마지막 배치 변경 되돌리기 (…)" label, or null when there is nothing to undo.</summary>
    internal Func<string?>? LatestLayoutUndoLabelProvider { get; set; }

    public event Action<FenceWindow, int>? SendToPageRequested;
    public event Action<FenceWindow, Guid>? RenameTemplateRequested;
    public event Action<FenceWindow, Guid>? DeleteTemplateRequested;
    public event Action? GlobalSearchRequested;
    public event Action? PeekEndRequested;
    public event Action? LayoutUndoRequested;

    // In a Folder Portal tab every item shares the portal's current folder, so that part is
    // left out too; only the name, the path below that folder and the type are searched.
    private IEnumerable<string?> GetSearchExcludedRoots()
    {
        var portalPath = SelectedTab is { Kind: DeckTabKind.FolderPortal } portal ? portal.PortalPath : null;
        return string.IsNullOrWhiteSpace(portalPath)
            ? SearchExcludedDesktopRoots
            : SearchExcludedDesktopRoots.Prepend(portalPath);
    }

    private static string[] ReadSearchExcludedDesktopRoots()
    {
        try
        {
            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // ---- Send to another desktop page (#9) ----

    private MenuItem BuildSendToPageMenu()
    {
        var canMove = CapabilityAllowed("Layout.MoveResizeGroups");
        var root = new MenuItem { Header = "다른 페이지로 보내기", IsEnabled = canMove && !Model.Locked && SendToPageRequested is not null };
        if (Model.Locked)
        {
            // Same meaning as on every other move route: a locked group stays where it is.
            ToolTipService.SetShowOnDisabled(root, true);
            root.ToolTip = "'위치와 크기 잠금'이 켜져 있어 옮길 수 없습니다.";
        }
        var maxPage = Math.Max(Model.PageIndex, MaxPageIndexProvider?.Invoke() ?? Model.PageIndex);
        var currentPageNumber = Model.PageIndex + 1;
        root.Items.Add(MakeMenuSectionHeader(Model.ShowOnAllPages
            ? "현재: 모든 바탕화면 페이지"
            : $"현재: 바탕화면 페이지 {currentPageNumber}"));
        for (var page = 0; page <= maxPage; page++)
        {
            var target = page;
            var isHere = !Model.ShowOnAllPages && Model.PageIndex == page;
            var item = MakeCheckItem($"페이지 {page + 1}", isHere, () => SendToPageRequested?.Invoke(this, target));
            item.IsEnabled = !isHere;
            root.Items.Add(item);
        }
        root.Items.Add(new Separator());
        // The new page comes after the other groups' pages, so the page this group leaves never
        // stays behind as an empty page; -1 means the group is already alone on the last page.
        var newPage = NewPageTargetProvider?.Invoke(this) ?? maxPage + 1;
        if (newPage >= 0)
        {
            var newPageNumber = newPage + 1;
            root.Items.Add(MakeItem($"새 페이지로 보내기 (페이지 {newPageNumber})", () => SendToPageRequested?.Invoke(this, newPage)));
        }
        else
        {
            var alone = MakeItem("새 페이지로 보내기", () => { });
            alone.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(alone, true);
            alone.ToolTip = "이 그룹은 이미 마지막 페이지에 혼자 있습니다.";
            root.Items.Add(alone);
        }
        root.Items.Add(new Separator());
        var follow = MakeCheckItem("보낸 뒤 그 페이지로 이동", _settings.FollowGroupSentToPage, () =>
        {
            if (!CapabilityAllowed("Settings.EditGeneral")) return;
            _settings.FollowGroupSentToPage = !_settings.FollowGroupSentToPage;
            ModelChanged?.Invoke();
        });
        follow.IsEnabled = CapabilityAllowed("Settings.EditGeneral");
        root.Items.Add(follow);
        return root;
    }

    // ---- Template entries (#36) ----

    private MenuItem BuildTemplateEntryMenu(DeckGroupTemplate template, bool canCreateDelete)
    {
        var canEditTemplates = CapabilityAllowed("Templates.Edit");
        var templateId = template.Id;
        var entry = new MenuItem { Header = template.Name };
        var create = MakeItem("이 템플릿으로 새 그룹 만들기", () => CreateFromTemplateRequested?.Invoke(templateId));
        create.IsEnabled = canCreateDelete;
        entry.Items.Add(create);
        entry.Items.Add(new Separator());
        var rename = MakeItem("이름 바꾸기…", () => RenameTemplateRequested?.Invoke(this, templateId));
        rename.IsEnabled = canEditTemplates;
        entry.Items.Add(rename);
        var delete = MakeDangerItem("삭제…", null, () => DeleteTemplateRequested?.Invoke(this, templateId));
        delete.IsEnabled = canEditTemplates;
        entry.Items.Add(delete);
        return entry;
    }

    // ---- "메뉴만" tab style: the "탭이름 ▾" button opens the tab list (#23) ----

    private void OpenMenuOnlyTabList(FrameworkElement placementTarget)
    {
        // Same entries as the right-click "탭" submenu, moved into a drop-down under the button.
        var source = BuildTabsMenu(CapabilityAllowed("Settings.EditGroups"));
        var entries = source.Items.Cast<object>().ToList();
        source.Items.Clear();
        var menu = CreateThemedContextMenu();
        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.Bottom;
        foreach (var entry in entries) menu.Items.Add(entry);
        menu.IsOpen = true;
    }

    // ---- Drag to a screen edge: preview, dwell and seam fitting (#3) ----

    private void ResetPageEdgeDropTracking()
    {
        _pageEdgeHintDirection = 0;
        _pageEdgeHintSince = 0;
        _pageEdgeHintTimer?.Stop();
        HidePageEdgeDropPreview();
    }

    private void UpdatePageEdgeDropPreview()
    {
        int direction;
        try { direction = PageEdgeDropDirectionResolver?.Invoke(this) ?? 0; }
        catch { direction = 0; }

        if (direction == _pageEdgeHintDirection)
        {
            if (_pageEdgeHintPopup is { IsOpen: true } popup)
            {
                // A popup does not follow its moving owner window; nudge it to re-place.
                var offset = popup.HorizontalOffset;
                popup.HorizontalOffset = offset + 1;
                popup.HorizontalOffset = offset;
            }
            return;
        }

        _pageEdgeHintDirection = direction;
        _pageEdgeHintSince = direction == 0 ? 0 : Environment.TickCount64;
        _pageEdgeHintTimer?.Stop();
        HidePageEdgeDropPreview();
        if (direction == 0) return;

        // The hint appears exactly when a drop would switch pages: after the dwell time.
        if (_pageEdgeHintTimer is null)
        {
            _pageEdgeHintTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(GroupPageDropEdgePolicy.DwellMilliseconds)
            };
            _pageEdgeHintTimer.Tick += (_, _) =>
            {
                _pageEdgeHintTimer?.Stop();
                if (_moving && _pageEdgeHintDirection != 0) ShowPageEdgeDropPreview(_pageEdgeHintDirection);
            };
        }
        _pageEdgeHintTimer.Start();
    }

    private void ShowPageEdgeDropPreview(int direction)
    {
        var targetPageNumber = Math.Max(0, Model.PageIndex + direction) + 1;
        var text = direction > 0
            ? $"놓으면 페이지 {targetPageNumber}로 이동 →"
            : $"← 놓으면 페이지 {targetPageNumber}로 이동";
        if (_pageEdgeHintPopup is null || _pageEdgeHintText is null)
        {
            var highContrast = SystemParameters.HighContrast;
            _pageEdgeHintText = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = highContrast ? SystemColors.InfoTextBrush : new SolidColorBrush(Color.FromRgb(244, 246, 250))
            };
            var border = new Border
            {
                Child = _pageEdgeHintText,
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = highContrast ? SystemColors.InfoBrush : new SolidColorBrush(Color.FromArgb(238, 27, 29, 34)),
                BorderBrush = highContrast ? SystemColors.WindowFrameBrush : new SolidColorBrush(Color.FromRgb(93, 145, 216))
            };
            _pageEdgeHintPopup = new Popup
            {
                Child = border,
                AllowsTransparency = true,
                PlacementTarget = Shell,
                Placement = PlacementMode.Center,
                StaysOpen = true,
                Focusable = false,
                IsHitTestVisible = false
            };
        }
        _pageEdgeHintText.Text = text;
        _pageEdgeHintPopup.IsOpen = true;
        if (!_pageEdgeHintVisualActive)
        {
            _pageEdgeHintVisualActive = true;
            SetTouchFlickTargetPreview(true);
        }
    }

    private void HidePageEdgeDropPreview()
    {
        if (_pageEdgeHintPopup is not null) _pageEdgeHintPopup.IsOpen = false;
        if (!_pageEdgeHintVisualActive) return;
        _pageEdgeHintVisualActive = false;
        SetTouchFlickTargetPreview(false);
    }

    /// <summary>True only when the drop pointer rested on that same page edge for the dwell time; resets tracking.</summary>
    internal bool TakePageEdgeDropDwell(int dropDirection)
    {
        var satisfied = GroupPageDropEdgePolicy.IsDwellSatisfied(
            _pageEdgeHintDirection, _pageEdgeHintSince, dropDirection, Environment.TickCount64);
        _pageEdgeHintDirection = 0;
        _pageEdgeHintSince = 0;
        _pageEdgeHintTimer?.Stop();
        return satisfied;
    }

    /// <summary>
    /// A group dropped across the seam between two monitors is moved fully inside the monitor
    /// that holds most of it. Groups on a single monitor (or partly off the outer edge) stay put.
    /// </summary>
    internal bool FitIntoDominantMonitor()
    {
        if (!IsLoaded || Model.Locked || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            var window = new DesktopPixelRect(
                (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y));
            var workAreas = Forms.Screen.AllScreens
                .Select(screen => screen.WorkingArea)
                .Select(work => new DesktopPixelRect(work.Left, work.Top, work.Right, work.Bottom))
                .ToList();
            var best = GroupPageDropEdgePolicy.FindDominantWorkArea(window, workAreas, out var intersecting);
            if (best < 0 || intersecting < 2) return false;
            var (dx, dy) = GroupPageDropEdgePolicy.FitInside(window, workAreas[best]);
            if (dx == 0 && dy == 0) return false;
            var dpi = VisualTreeHelper.GetDpi(this);
            Left += dx / Math.Max(0.5, dpi.DpiScaleX);
            Top += dy / Math.Max(0.5, dpi.DpiScaleY);
            if (_settings.EnableSmartGroupSnapping) SnapToMonitorEdges();
            Model.X = Left;
            Model.Y = Top;
            RefreshWallpaperBlur();
            return true;
        }
        catch { return false; }
    }

    // ---- Location highlight for new / sent / revealed groups (#17, #9, #26) ----

    internal void FlashLocationHighlight(int milliseconds = 1600)
    {
        if (_closingFromController) return;
        if (!_locationHighlightActive)
        {
            _locationHighlightActive = true;
            SetTouchFlickTargetPreview(true);
        }
        if (_locationHighlightTimer is null)
        {
            _locationHighlightTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _locationHighlightTimer.Tick += (_, _) =>
            {
                _locationHighlightTimer?.Stop();
                if (!_locationHighlightActive) return;
                _locationHighlightActive = false;
                SetTouchFlickTargetPreview(false);
            };
        }
        _locationHighlightTimer.Stop();
        _locationHighlightTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 200, 10000));
        _locationHighlightTimer.Start();
    }

    // ---- Reveal an item found by the global search (#26) ----

    internal void RevealItem(Guid tabId, string path)
    {
        var index = Model.Tabs.FindIndex(t => t.Id == tabId);
        if (index < 0) return;
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = "";
            SearchPanel.Visibility = Visibility.Collapsed;
        }
        if (Model.RolledUp) SetRollupPreview(true);
        if (Model.SelectedTabIndex != index) SelectTab(Model.Tabs[index]);
        else EnsureItemsRendered();

        if (_peekMode)
        {
            // Peek windows are topmost; keyboard focus lets Enter open the selected item.
            try { Activate(); } catch { }
        }

        _revealPath = path;
        _revealTicks = 0;
        _revealFound = false;
        if (_revealTimer is null)
        {
            _revealTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _revealTimer.Tick += RevealTimer_Tick;
        }
        _revealTimer.Stop();
        _revealTimer.Start();
        FlashLocationHighlight(2400);
    }

    private void RevealTimer_Tick(object? sender, EventArgs e)
    {
        _revealTicks++;
        // Items of a large tab or a Folder Portal load asynchronously; retry for about 3 seconds.
        if (!_revealFound && _revealPath is not null) _revealFound = TrySelectRevealedItem(_revealPath);
        if (_revealFound || _revealTicks >= 25 || _closingFromController)
        {
            _revealTimer?.Stop();
            _revealPath = null;
        }
    }

    private bool TrySelectRevealedItem(string path)
    {
        if (ItemsList.Visibility == Visibility.Visible)
        {
            var tile = ItemsList.Items.OfType<ItemTile>()
                .FirstOrDefault(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
            if (tile is null) return false;
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItem = tile;
            ItemsList.ScrollIntoView(tile);
            return true;
        }

        foreach (var border in _freeBorders.Values)
        {
            if (border.Tag is not ItemTile tile ||
                !string.Equals(tile.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
            SelectFreeTile(border, tile);
            border.BringIntoView();
            return true;
        }
        return false;
    }
}
