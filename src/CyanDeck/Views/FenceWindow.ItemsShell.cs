using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CyanDeck.Appearance;
using CyanDeck.Models;
using CyanDeck.Services;

namespace CyanDeck.Views;

/// <summary>
/// Group item commands shared by the mouse, the keyboard and both context menus:
/// one open path (counted, capped/confirmed for large selections), the Windows Shell
/// menu boundary for real Desktop files inside a group, coalesced Folder Portal
/// refreshes that keep the selection, and the Recycle Bin icon refresh.
/// </summary>
public partial class FenceWindow
{
    private DispatcherTimer? _portalChangeTimer;
    private readonly HashSet<string> _portalChangedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _portalChangePending;
    private DateTime _portalChangeFirstUtc;
    private string? _portalViewKey;
    private object? _portalViewSource;

    // ---------------------------------------------------------------- open path

    /// <summary>
    /// The single "open" path for Enter, double-click, the CyanDeck batch menu and the
    /// Windows menu "open" verb. Opens are counted for the "열기 횟수" sort. Selections at
    /// or above the large-batch threshold are confirmed when that setting is on; when it is
    /// off, selections above the safety cap are capped with a notice, never silently dropped.
    /// </summary>
    private void OpenTilesFromUser(IReadOnlyList<ItemTile> tiles)
    {
        var targets = tiles
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Path))
            .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0) return;
        if (targets.Count == 1)
        {
            OpenTileFromUser(targets[0]);
            return;
        }

        var plan = BatchOpenPolicy.Plan(targets.Count, _settings.ConfirmLargeBatchOperations, _settings.LargeBatchOperationThreshold);
        if (plan.Decision == BatchOpenDecision.ConfirmThenOpenAll)
        {
            var question = $"선택한 {plan.TotalCount}개 항목을 모두 여시겠습니까?";
            if (System.Windows.MessageBox.Show(this, question, "CyanDeck — 대량 작업 확인",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }

        var tab = SelectedTab;
        var counted = false;
        Exception? firstFailure = null;
        foreach (var tile in targets.Take(plan.OpenCount))
        {
            var failure = LaunchTile(tile);
            if (failure is not null)
            {
                firstFailure ??= failure;
                continue;
            }
            if (tab?.Kind == DeckTabKind.Collection && tile.Source.OpenCount < long.MaxValue)
            {
                tile.Source.OpenCount++;
                counted = true;
            }
        }

        if (counted)
        {
            ModelChanged?.Invoke();
            if (tab is not null && ReferenceEquals(SelectedTab, tab) && tab.Sort == DeckSortMode.OpenCount)
                RefreshItems(IconMovementAnimationContext.SortOrArrange);
        }

        if (firstFailure is ShortcutLaunchException launchFailure) ShortcutLaunchService.ReportFailure(launchFailure);
        else if (firstFailure is not null)
            System.Windows.MessageBox.Show(this, firstFailure.Message, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (plan.Decision == BatchOpenDecision.OpenFirstAndNotify)
        {
            var notice = $"선택한 {plan.TotalCount}개 중 처음 {plan.OpenCount}개만 열었습니다.\n" +
                // Names the existing Settings checkbox (성능 > 대량 작업·캐시) so the user can find it.
                "모두 열려면 설정 > 대량 작업·캐시에서 '대량 휴지통 작업 전에 추가 확인'을 켜 주세요. 켜 두면 열기 전에 한 번 묻고 모두 엽니다.";
            System.Windows.MessageBox.Show(this, notice, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>One item: Folder Portal folders navigate (or open) like a double-click.</summary>
    private void OpenTileFromUser(ItemTile tile)
    {
        if (SelectedTab?.Kind == DeckTabKind.FolderPortal && Directory.Exists(tile.Path))
        {
            OpenPortalFolder(tile.Path);
            return;
        }
        OpenItem(tile);
    }

    /// <summary>Launches one item without UI side effects; returns the failure, if any.</summary>
    private static Exception? LaunchTile(ItemTile tile)
    {
        try
        {
            if (DesktopSystemItems.IsSupportedVirtualPath(tile.Path))
                DesktopSystemItems.OpenVirtualItem(tile.Path);
            else
                ShortcutLaunchService.Open(tile.Path);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // ------------------------------------------------------ Windows Shell menu

    /// <summary>
    /// Collection tiles are REFERENCES: native Shell verbs act on the original target.
    /// Like standalone desktop icons, a group shows the Windows menu only when every
    /// selected item is a real Desktop child; external references (C:\, Downloads…)
    /// keep the CyanDeck menu so the original source stays protected.
    /// </summary>
    private bool CanShowNativeShellMenu(IEnumerable<string> paths)
    {
        var tab = SelectedTab;
        if (tab is null) return false;
        if (tab.Kind != DeckTabKind.Collection) return true;
        var list = paths.ToList();
        return list.Count > 0 && list.All(DesktopRecycleService.IsActualDesktopItem);
    }

    /// <summary>
    /// Windows menu verbs that CyanDeck routes to its own command: "open" uses the shared
    /// counted open path, "rename" uses CyanDeck rename, and "delete" inside a group uses the
    /// guarded three-way choice (휴지통/그룹에서만 제거/취소) instead of the Shell's own delete.
    /// </summary>
    private bool TryHandleNativeShellVerb(string verb, IReadOnlyList<ItemTile> tiles)
    {
        if (tiles.Count == 0) return false;
        if (string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase))
        {
            OpenTilesFromUser(tiles);
            return true;
        }
        if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
        {
            RenameItem(tiles[0]);
            return true;
        }
        if (string.Equals(verb, "delete", StringComparison.OrdinalIgnoreCase) && SelectedTab?.Kind == DeckTabKind.Collection)
        {
            DeleteItems(tiles);
            return true;
        }
        return false;
    }

    /// <summary>
    /// After a Windows menu closes, drop only the touched group references whose Desktop
    /// file is gone (for example moved by a Shell extension). Unrelated items are never
    /// pruned: an unplugged drive is not a deletion.
    /// </summary>
    private void SchedulePostShellMenuRefresh(DeckTab? tab, IReadOnlyList<ItemTile> touched)
    {
        var ids = touched.Select(t => t.Source.Id).ToHashSet();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (tab?.Kind == DeckTabKind.Collection)
                tab.Items.RemoveAll(i => ids.Contains(i.Id) && !File.Exists(i.Path) && !Directory.Exists(i.Path)
                    && !DesktopSystemItems.IsSupportedVirtualPath(i.Path));
            RefreshItems(IconMovementAnimationContext.ItemSetChanged);
            ModelChanged?.Invoke();
            RecycleBinStateMonitor.RequestCheck();
        }));
    }

    // ------------------------------------------------------------ Recycle Bin

    /// <summary>Reloads the visible tab when it shows the Recycle Bin (empty/full changed).</summary>
    internal void RefreshRecycleBinIcon()
    {
        var tab = SelectedTab;
        if (tab is null || tab.Kind != DeckTabKind.Collection) return;
        if (!tab.Items.Any(i => DesktopSystemItems.IsRecycleBinPath(i.Path))) return;
        RefreshItems(IconMovementAnimationContext.ExternalRefresh);
    }

    // ------------------------------------------------------ Folder Portal burst

    /// <summary>
    /// Folder Portal watcher callback. Bursts of changes (downloads, extraction, builds)
    /// are collected and refreshed once after a short quiet period, at most every 2 s.
    /// </summary>
    private void QueuePortalWatcherRefresh(string? changedPath, string? oldPath)
    {
        if (!string.IsNullOrWhiteSpace(changedPath)) _portalChangedPaths.Add(changedPath);
        if (!string.IsNullOrWhiteSpace(oldPath)) _portalChangedPaths.Add(oldPath);
        // The cache is marked dirty only when the batch is flushed: a dirty flag set here
        // would make an in-flight load ("changed while loading") reload immediately and
        // bypass the quiet period and the 2 s cap during long bursts.
        var now = DateTime.UtcNow;
        if (!_portalChangePending)
        {
            _portalChangePending = true;
            _portalChangeFirstUtc = now;
        }
        var delay = FolderPortalRefreshCoalescingPolicy.NextDelay(_portalChangeFirstUtc, now, now);
        if (_portalChangeTimer is null)
        {
            _portalChangeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _portalChangeTimer.Tick += (_, _) => FlushPortalWatcherRefresh();
        }
        _portalChangeTimer.Stop();
        _portalChangeTimer.Interval = delay < TimeSpan.FromMilliseconds(15) ? TimeSpan.FromMilliseconds(15) : delay;
        _portalChangeTimer.Start();
    }

    private void FlushPortalWatcherRefresh()
    {
        _portalChangeTimer?.Stop();
        if (!_portalChangePending) return;
        _portalChangePending = false;
        foreach (var path in _portalChangedPaths) _icons.Invalidate(path);
        _portalChangedPaths.Clear();
        _portalCacheDirty = true;
        if (!IsLoaded) return;
        RefreshItems(IconMovementAnimationContext.ExternalRefresh);
    }

    /// <summary>
    /// Replaces the Folder Portal list while keeping the selected items and the scroll
    /// position when the same folder is merely refreshed (not on tab/folder changes).
    /// </summary>
    private void SetPortalItemsSourcePreservingView(DeckTab tab, List<ItemTile> sorted)
    {
        var viewKey = tab.Id.ToString("N") + "|" + (_portalCachePath ?? tab.PortalPath ?? string.Empty);
        // Same folder AND the list on screen is still the one this method put there
        // (not a Collection tab shown in between).
        var sameView = ItemsList.ItemsSource is not null
            && ReferenceEquals(ItemsList.ItemsSource, _portalViewSource)
            && string.Equals(_portalViewKey, viewKey, StringComparison.OrdinalIgnoreCase);
        _portalViewKey = viewKey;
        _portalViewSource = sorted;
        if (!sameView)
        {
            ItemsList.ItemsSource = sorted;
            return;
        }

        var selectedKeys = ItemsList.SelectedItems.OfType<ItemTile>()
            .Select(t => t.StableItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scroll = FindVisualChild<ScrollViewer>(ItemsList);
        var verticalOffset = scroll?.VerticalOffset ?? 0d;
        var horizontalOffset = scroll?.HorizontalOffset ?? 0d;

        ItemsList.ItemsSource = sorted;
        if (selectedKeys.Count > 0)
        {
            foreach (var tile in sorted)
                if (selectedKeys.Contains(tile.StableItemId)) ItemsList.SelectedItems.Add(tile);
        }
        if (scroll is not null && (verticalOffset > 0 || horizontalOffset > 0))
        {
            ItemsList.UpdateLayout();
            scroll.ScrollToHorizontalOffset(horizontalOffset);
            scroll.ScrollToVerticalOffset(verticalOffset);
        }
    }
}
