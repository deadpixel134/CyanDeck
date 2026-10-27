#!/usr/bin/env python3
"""v70: group items, system icons and the Windows menu.
#10 Recycle Bin empty/full icon refresh, #12 Windows menu for real Desktop files inside groups,
#18 one counted open path without silent 12-item cut and a Korean batch header,
#21 DPI-scaled first-free-cell placement of new loose desktop icons,
#22 Folder Portal sort labels and most-opened-first default, #32 coalesced Folder Portal refresh
that keeps the selection, #37 folder flag cached per tile instead of repeated disk probes.
Non-destructive static checks; native menus, DPI and timing are verified on Windows by hand."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

fence = read("src/CyanDeck/Views/FenceWindow.xaml.cs")
items_shell = read("src/CyanDeck/Views/FenceWindow.ItemsShell.cs")
keyboard = read("src/CyanDeck/Views/FenceWindow.Keyboard.cs")
group_menu = read("src/CyanDeck/Views/FenceWindow.ContextMenu.cs")
loose = read("src/CyanDeck/Services/LooseDesktopIconHost.cs")
icons = read("src/CyanDeck/Services/IconService.cs")
monitor = read("src/CyanDeck/Services/RecycleBinStateMonitor.cs")
app = read("src/CyanDeck/Services/AppController.cs")
app_items = read("src/CyanDeck/Services/AppController.ItemsShell.cs")
recycler = read("src/CyanDeck/Services/DesktopRecycleService.cs")
open_policy = read("src/CyanDeck/Services/BatchOpenPolicy.cs")
coalescing = read("src/CyanDeck/Services/FolderPortalRefreshCoalescingPolicy.cs")
planner = read("src/CyanDeck/Layout/LooseDesktopAutoPlacementPlanner.cs")
harness = read("tests/FenceVisibilityHarness/Program.cs")
harness_project = read("tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj")

shell_gate = items_shell.split("private bool CanShowNativeShellMenu(", 1)[1].split("private bool TryHandleNativeShellVerb(", 1)[0]
shell_verbs = items_shell.split("private bool TryHandleNativeShellVerb(", 1)[1].split("private void SchedulePostShellMenuRefresh(", 1)[0]
post_menu = items_shell.split("private void SchedulePostShellMenuRefresh(", 1)[1].split("internal void RefreshRecycleBinIcon()", 1)[0]
portal_queue = items_shell.split("private void QueuePortalWatcherRefresh(", 1)[1].split("private void FlushPortalWatcherRefresh()", 1)[0]
portal_flush = items_shell.split("private void FlushPortalWatcherRefresh()", 1)[1].split("private void SetPortalItemsSourcePreservingView(", 1)[0]
open_path = items_shell.split("private void OpenTilesFromUser(", 1)[1].split("private void OpenTileFromUser(", 1)[0]
item_tile = fence.split("public sealed class ItemTile", 1)[1]
ensure_position = loose.split("private void EnsurePosition(", 1)[1].split("private List<Rectangle> CollectAutoPlacementBlockers(", 1)[0]

checks = {
    # 10
    "Recycle Bin monitor uses throttled shell notifications plus a background SHQueryRecycleBin check":
        "SHChangeNotifyRegister(" in monitor and "SHCNE_ALLEVENTS" in monitor and "CSIDL_BITBUCKET = 0x000A" in monitor and
        "await Task.Run(() => IconService.QueryRecycleBinHasItems());" in monitor and
        "if (!_checkTimer.IsEnabled) _checkTimer.Start();" in monitor and
        "PollIntervalWithShellNotifications = TimeSpan.FromSeconds(60);" in monitor and
        "SHChangeNotifyDeregister(_notifyRegistration)" in monitor and
        "internal static bool? QueryRecycleBinHasItems()" in icons,
    "only an empty/full change reloads the Recycle Bin icon in the desktop surrogate and groups":
        "if (previous is not null && previous.Value != hasItems.Value)" in monitor and
        "_icons.Invalidate(DesktopSystemItems.RecycleBinShellPath);" in app_items and
        "_looseDesktopHost?.RefreshIcon(DesktopSystemItems.RecycleBinShellPath);" in app_items and
        "window.RefreshRecycleBinIcon();" in app_items and "internal void RefreshRecycleBinIcon()" in items_shell,
    "monitor starts with the desktop runtime, skips safe mode and is disposed on both exit paths":
        "StartRecycleBinStateMonitor();" in app and app.count("StopRecycleBinStateMonitor();") >= 2 and
        "if (_safeMode || _disposed || _recycleBinMonitor is not null) return;" in app_items,
    "CyanDeck recycle actions and the Recycle Bin menu ask for an immediate re-check":
        "if (recycled.Count > 0) RecycleBinStateMonitor.RequestCheck();" in recycler and
        "RecycleBinStateMonitor.RequestCheck();" in loose and "RecycleBinStateMonitor.RequestCheck();" in post_menu,
    # 12
    "groups show the Windows menu only when every item is a real Desktop child":
        "if (tab.Kind != DeckTabKind.Collection) return true;" in shell_gate and
        "list.All(DesktopRecycleService.IsActualDesktopItem)" in shell_gate and
        "CanShowNativeShellMenu([tile.Path]) &&" in fence and
        "CanShowNativeShellMenu(existing.Select(t => t.Path)) &&" in fence and
        "SelectedTab?.Kind != DeckTabKind.Collection &&" not in fence,
    "revived group-only Windows menu commands are Korean":
        "Remove from CyanDeck group" not in fence and "Remove selected from CyanDeck group" not in fence and
        'extras.Add(new("그룹에서만 제거 (원본 유지)"' in fence and
        'extras.Add(new("선택 항목을 그룹에서만 제거 (원본 유지)"' in fence and
        'extras.Add(new("파일 탐색기에서 표시", () => Reveal(tile.Path)));' in fence and
        'extras.Add(new("첫 항목을 파일 탐색기에서 표시", () => Reveal(existing[0].Path)));' in fence,
    "Windows menu open/rename/group delete are routed to CyanDeck commands":
        '"open"' in shell_verbs and "OpenTilesFromUser(tiles);" in shell_verbs and
        '"rename"' in shell_verbs and "RenameItem(tiles[0]);" in shell_verbs and
        '"delete"' in shell_verbs and "SelectedTab?.Kind == DeckTabKind.Collection" in shell_verbs and "DeleteItems(tiles);" in shell_verbs and
        "Func<string, bool>? verbInterceptor = verb => TryHandleNativeShellVerb(verb, [tile]);" in fence,
    "after the Windows menu only touched references whose file is gone are dropped":
        "tab.Items.RemoveAll(i => ids.Contains(i.Id) && !File.Exists(i.Path) && !Directory.Exists(i.Path)" in post_menu and
        "SelectedTab.Items.RemoveAll(i => !File.Exists(i.Path)" not in fence and
        "SchedulePostShellMenuRefresh(SelectedTab, existing);" in fence and "SchedulePostShellMenuRefresh(SelectedTab, [tile]);" in fence,
    # 18
    "batch menu header is Korean":
        'Header = $"{existing.Count}개 선택됨"' in fence and "items selected" not in fence,
    "no silent 12-item cut in menus, Windows menu or Enter":
        "Take(12)" not in fence and "Take(12)" not in keyboard and
        'MakeItem("선택 항목 열기", () => OpenTilesFromUser(existing))' in fence and
        "Func<string, bool>? batchVerbInterceptor = verb => TryHandleNativeShellVerb(verb, existing);" in fence,
    "large selections reuse the large-batch confirmation or announce the cap":
        "BatchOpenPolicy.Plan(targets.Count, _settings.ConfirmLargeBatchOperations, _settings.LargeBatchOperationThreshold)" in open_path and
        "모두 여시겠습니까?" in open_path and "MessageBoxResult.No) != MessageBoxResult.Yes" in open_path and
        "처음 {plan.OpenCount}개만 열었습니다." in open_path and
        "internal const int UnconfirmedOpenLimit = 12;" in open_policy and "Math.Clamp(largeBatchThreshold, 5, 500)" in open_policy and
        # the user's threshold is checked before the 12-item auto-open limit
        open_policy.index("if (confirmLargeBatches && total >= threshold)") < open_policy.index("if (total <= UnconfirmedOpenLimit)") and
        # The notice names the redesigned Settings location and checkbox label.
        "설정 > 그룹 동작 > 대량 작업 확인에서 '많은 항목을 열거나 휴지통으로 보낼 때 확인'을 켜 주세요." in open_path,
    "Enter and double-click use the same counted open path":
        "if (selected.Count > 1) OpenTilesFromUser(selected);" in keyboard and
        "else if (tile is not null) OpenTilesFromUser([tile]);" in keyboard and
        "Open(tile.Path)" not in keyboard and
        "tile.Source.OpenCount++;" in open_path and
        "// Same open path as Enter and the menus (portal folders navigate, opens are counted).\n        OpenTileFromUser(tile);" in fence,
    # 21
    "new loose icons use DPI-scaled cells and the first free cell":
        "var scale = GetMonitorScale(area);" in ensure_position and
        "LooseDesktopAutoPlacementPlanner.FindSlot(area, scale, CollectAutoPlacementBlockers(item), ordinal)" in ensure_position and
        "var rows = Math.Max(1, (area.Height - edgeInset * 2) / slotHeight);" not in loose and
        "GetDpiForMonitor(monitor, 0, out var dpiX, out _)" in loose and
        "ScaleDip(LooseDesktopIconLayoutProfile.LooseAutoCellWidthDip, s)" in planner and
        "if (!blockers.Any(b => b.IntersectsWith(candidate))) return new Point(x, y);" in planner,
    "auto placement avoids other icons and group windows, never moves saved positions":
        "if (item.PositionX is double x && item.PositionY is double y && double.IsFinite(x) && double.IsFinite(y)) return;" in loose and
        "AutoPlacementObstacles?.Invoke()" in loose and "_autoPlacementPeers = valid.Values.ToArray();" in loose and
        "_looseDesktopHost.AutoPlacementObstacles = GetLooseAutoPlacementObstacles;" in app and
        "window.TryGetPhysicalBounds(out var bounds)" in app_items,
    # 22
    "Folder Portal sort menu: 만든 날짜, disabled open count, most-opened first when newly chosen":
        'portalSort ? "만든 날짜" : "추가한 날짜"' in group_menu and
        "열기 횟수 (폴더 포털에서는 사용할 수 없음)" in group_menu and
        "if (portalSort && mode == DeckSortMode.OpenCount) sortItem.IsEnabled = false;" in group_menu and
        "t.SortDescending = local == DeckSortMode.OpenCount;" in group_menu,
    "Folder Portal 'added' sort uses the file creation time":
        "DeckSortMode.Added => i => i.IsFolderPortalItem ? i.CreatedUtc : i.Source.AddedUtc," in fence and
        "createdUtc = fi.CreationTimeUtc;" in fence and "createdUtc = di.CreationTimeUtc;" in fence,
    # 32
    "Folder Portal change bursts are coalesced (0.4 s quiet, 2 s cap)":
        "(changedPath, oldPath) => QueuePortalWatcherRefresh(changedPath, oldPath)," in fence and
        "QuietPeriod = TimeSpan.FromMilliseconds(400)" in coalescing and "MaximumDelay = TimeSpan.FromSeconds(2)" in coalescing and
        "FolderPortalRefreshCoalescingPolicy.NextDelay(_portalChangeFirstUtc, now, now)" in items_shell and
        "foreach (var path in _portalChangedPaths) _icons.Invalidate(path);" in items_shell and
        # dirty only on flush, so events during a load do not trigger an immediate reload
        "_portalCacheDirty = true;" not in portal_queue and "_portalCacheDirty = true;" in portal_flush,
    "Folder Portal refresh keeps the selection and scroll position":
        "SetPortalItemsSourcePreservingView(tab, sorted);" in fence and
        "ItemsList.SelectedItems.Add(tile)" in items_shell and
        "scroll.ScrollToVerticalOffset(verticalOffset);" in items_shell and
        "ReferenceEquals(ItemsList.ItemsSource, _portalViewSource)" in items_shell,
    "Folder Portal existence probe moved off the UI thread":
        "return (Tiles: new List<ItemTile>(), Error: (Exception?)null, Missing: true);" in fence and
        "if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))" not in fence and
        "if (string.Equals(_portalWatch.CurrentPath, fullPath, StringComparison.OrdinalIgnoreCase)) return;" in fence,
    # 37
    "folder flag computed once per tile and reused by type, size, sort and search":
        "public bool IsDirectory { get; init; }" in item_tile and
        "public string Type => _type ??= IsDirectory ?" in item_tile and
        'public string SizeText => IsDirectory ? "" : FormatSize(Size);' in item_tile and
        "Directory.Exists(Path)" not in item_tile and
        "source.OrderBy(i => i.IsDirectory ? 0 : 1)" in fence and
        "IsDirectory = isDirectory," in fence,
    # tests
    "pure policies are covered by the harness":
        all(f'src/CyanDeck/{p}' in harness_project for p in (
            "Services/BatchOpenPolicy.cs", "Services/FolderPortalRefreshCoalescingPolicy.cs",
            "Layout/LooseDesktopAutoPlacementPlanner.cs", "Layout/LooseDesktopIconLayoutProfile.cs")) and
        "PASS: {v70OpenCases.Length} batch open checks" in harness and
        "PASS: 4 Folder Portal refresh coalescing checks" in harness and
        "PASS: 6 loose icon auto-placement checks" in harness,
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
