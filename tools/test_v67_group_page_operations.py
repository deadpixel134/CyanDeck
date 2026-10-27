#!/usr/bin/env python3
"""v8.0.44: group, page and template operations. Dropping a group near a monitor edge no longer
switches pages (pointer edge + dwell, seam rules shared with the page gesture, seam-straddling
groups fitted into one monitor), groups can be sent to another or a new page from their menu,
new groups land at a free spot of the right monitor, templates can be renamed/deleted one by one
and the 50-template limit asks first, the "메뉴만" tab button opens the tab list, opening an item
ends Peek, and a dark "항목 찾기" window searches every group, tab and page.
Non-destructive static checks; the behaviour itself is verified on Windows by hand."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

app = read("src/CyanDeck/Services/AppController.cs")
ops = read("src/CyanDeck/Services/AppController.GroupPageOperations.cs")
lifecycle = read("src/CyanDeck/Services/AppController.FenceWindowLifecycle.cs")
fence = read("src/CyanDeck/Views/FenceWindow.xaml.cs")
fence_ops = read("src/CyanDeck/Views/FenceWindow.GroupPageOperations.cs")
menu = read("src/CyanDeck/Views/FenceWindow.ContextMenu.cs")
keyboard = read("src/CyanDeck/Views/FenceWindow.Keyboard.cs")
policy = read("src/CyanDeck/Services/GroupPageOperationsPolicy.cs")
search_policy = read("src/CyanDeck/Services/GlobalItemSearchPolicy.cs")
search_window = read("src/CyanDeck/Views/GlobalItemSearchWindow.cs")
launch = read("src/CyanDeck/Services/ShortcutLaunchService.cs")
system_items = read("src/CyanDeck/Services/DesktopSystemItems.cs")
models = read("src/CyanDeck/Models/Models.cs")
routing = read("src/CyanDeck/Services/TrayMenuRoutingPolicy.cs")
schema = read("src/CyanDeck/Services/StateSchema.cs")
harness = read("tests/FenceVisibilityHarness/Program.cs")
harness_tests = read("tests/FenceVisibilityHarness/GroupPageOperationsTests.cs")
harness_project = read("tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj")

def body(text, start, end):
    return text.split(start, 1)[1].split(end, 1)[0]

try_move = body(app, "private bool TryMoveFenceAcrossPageEdge(FenceWindow source)", "private void RecoverVisibleSurfacesFromTray()")
move_completed = body(app, "private void OnFenceMoveCompleted(FenceWindow source)", "var sourceRect =")
save_template = body(app, "private void SaveFenceTemplate(FenceWindow source)", "private void CreateFenceFromTemplate(Guid templateId)")
new_fence = body(app, "private void CreateNewFence()", "private void OnDeleteFence(FenceWindow window)")
from_template = body(app, "private void CreateFenceFromTemplate(Guid templateId)", "private void ImportDroppedIntoFence(")
resolve_drop = body(ops, "private int ResolveFenceDropPageDirection(FenceWindow source)", "private void FitMovedFenceIntoDominantMonitor(")
send_page = body(ops, "private void SendFenceToPage(FenceWindow window, int requestedPage)", "private void AddTrayPageCommands(")
tray_pages = body(ops, "private void AddTrayPageCommands(", "private void CreateNewFenceOnNewPage(")
tray_templates = body(ops, "private Forms.ToolStripMenuItem BuildTrayTemplateMenu(", "private void RenameFenceTemplate(")
rename_template = body(ops, "private void RenameFenceTemplate(Guid templateId, Window? owner)", "private void DeleteFenceTemplate(")
delete_template = body(ops, "private void DeleteFenceTemplate(Guid templateId, Window? owner)", "private void ConfirmDeleteAllTemplates()")
open_search = body(ops, "private void OpenGlobalItemSearch()", "private async Task LoadPortalSearchEntriesAsync(")
reveal = body(ops, "private void RevealGlobalSearchResult(GlobalItemSearchEntry entry)", "private void OnItemLaunchedEndPeek()")
end_peek = ops.split("private void OnItemLaunchedEndPeek()", 1)[1]
populate = body(menu, "private void PopulateGroupContextMenu(ContextMenu menu)", "private MenuItem BuildTabsMenu(")
behavior = body(menu, "private MenuItem BuildBehaviorMenu()", "private MenuItem BuildAutomationMenu()")
group_tools = body(menu, "private MenuItem BuildGroupToolsMenu(bool canCreateDelete)", "private void RenameGroup()")
send_menu = body(fence_ops, "private MenuItem BuildSendToPageMenu()", "private MenuItem BuildTemplateEntryMenu(")
template_entry = body(fence_ops, "private MenuItem BuildTemplateEntryMenu(", "private void OpenMenuOnlyTabList(")
tab_list = body(fence_ops, "private void OpenMenuOnlyTabList(FrameworkElement placementTarget)", "private void ResetPageEdgeDropTracking()")
fit = body(fence_ops, "internal bool FitIntoDominantMonitor()", "internal void FlashLocationHighlight(")
reveal_item = fence_ops.split("internal void RevealItem(Guid tabId, string path)", 1)[1]
matches_search = body(fence, "private bool MatchesSearch(ItemTile tile)", "private void UpdateSearchCount(")
mouse_move = body(fence, "private void Fence_PreviewMouseMove(", "private void Fence_PreviewMouseLeftButtonUp(")

checks = {
    # #3 dropping near a monitor edge
    "page switch on drop uses the release pointer and a dwell, not the snapped group geometry":
        "var direction = ResolveFenceDropPageDirection(source);" in try_move and
        "if (!source.TakePageEdgeDropDwell(direction)) return false;" in try_move and
        "GetPageEdgeDirection()" not in try_move,
    "drop edges use the page gesture's outer-edge and monitor-seam classification":
        "topology.ClassifyHorizontalEdgeAtScreenY(surfaceId, edge, cursorY, options)" in policy and
        "DesktopPageEdgeNavigationDecision.AllowedInteriorSeam => direction," in policy and
        "_ => 0," in policy and
        "GetDesktopTopologyOptions());" in resolve_drop and
        '!IsManagedCapabilityAllowed("Layout.MoveResizeGroups") || !_state.Settings.EnableEdgePageGestures' in resolve_drop,
    "dragging to a page edge shows a 'move to page N' preview after the dwell":
        "ResetPageEdgeDropTracking();" in mouse_move and "UpdatePageEdgeDropPreview();" in mouse_move and
        "Interval = TimeSpan.FromMilliseconds(GroupPageDropEdgePolicy.DwellMilliseconds)" in fence_ops and
        "놓으면 페이지 {targetPageNumber}로 이동" in fence_ops and
        "if (_moving && _pageEdgeHintDirection != 0) ShowPageEdgeDropPreview(_pageEdgeHintDirection);" in fence_ops,
    "a group dropped across a monitor seam is fitted into the monitor holding most of it":
        move_completed.index("if (TryMoveFenceAcrossPageEdge(source)) return;") <
        move_completed.index("FitMovedFenceIntoDominantMonitor(source);") and
        "if (best < 0 || intersecting < 2) return false;" in fit and
        "GroupPageDropEdgePolicy.FitInside(window, workAreas[best])" in fit,
    "two-monitor drop coordinates are regression-tested in the compiled harness":
        "GroupPageOperationsTests.Run();" in harness and
        "Topology/DesktopMonitorTopology.cs" in harness_project and
        "Services/GroupPageOperationsPolicy.cs" in harness_project and
        "Services/GlobalItemSearchPolicy.cs" in harness_project and
        "ResolveDirection(topology, Left, 1918, 500, seamBlocked) == 0" in harness_tests and
        "ResolveDirection(topology, Right, 3838, 500, seamBlocked) == 1" in harness_tests,
    # #9 send to page / new page
    "group menu sends a group to any page or a new page, replacing the disabled page line":
        "menu.Items.Add(BuildSendToPageMenu());" in populate and
        'Header = "다른 페이지로 보내기"' in send_menu and
        "새 페이지로 보내기 (페이지 {newPageNumber})" in send_menu and
        'MakeCheckItem("보낸 뒤 그 페이지로 이동", _settings.FollowGroupSentToPage' in send_menu and
        "표시 위치:" not in behavior,
    "sending checks the move policy, clamps to one new page and follows or fixes the current page":
        'EnsureManagedCapability("Layout.MoveResizeGroups", "그룹을 다른 페이지로 보내기")' in send_page and
        "GroupPageSendPolicy.ClampTarget(requestedPage, GetMaxPage())" in send_page and
        "GroupPageSendPolicy.ShouldFollow(_state.Settings.FollowGroupSentToPage" in send_page and
        "_state.CurrentPage = Math.Clamp(_state.CurrentPage, 0, GetMaxPage());" in send_page,
    "tray page menu has previous/next page and a new page with a group":
        "AddTrayPageCommands(pages, trayCursor);" in app and
        all(s in tray_pages for s in ('"이전 페이지"', '"다음 페이지"', '"새 페이지에 그룹 만들기"')),
    # #17 new group placement
    "new groups go to a free spot on the tray-pointer or source-group monitor":
        "GroupPlacementPolicy.FindFreeSpot(" in ops and
        "var trayCursor = Forms.Cursor.Position;" in app and
        "CreateNewFenceFromTray(trayCursor)" in app and
        "window.NewFenceRequested += () => CreateNewFenceNear(window);" in lifecycle and
        "window.CreateFromTemplateRequested += templateId => CreateFenceFromTemplateNear(templateId, window);" in lifecycle and
        "SystemParameters.WorkArea" not in new_fence and "SystemParameters.WorkArea" not in from_template,
    "a new group is highlighted, and a tray-created one explains Peek":
        "window?.FlashLocationHighlight();" in ops and
        "Peek(창 위로 그룹 보기)로 확인하세요" in ops,
    "tray 'move all groups' lists every monitor instead of the tray's monitor":
        "AddTrayMoveAllGroupsCommand(menu);" in app and
        '"그룹 전부 한 모니터로 옮기기"' in ops and
        '$"모니터 {i + 1}{primaryMark}"' in ops,
    # #36 templates
    "every template has create / rename / delete in the group menu, not gated by group creation":
        "fromTemplate.Items.Add(BuildTemplateEntryMenu(localTemplate, canCreateDelete));" in group_tools and
        'new MenuItem { Header = "저장한 템플릿" }' in group_tools and
        "create.IsEnabled = canCreateDelete;" in template_entry and
        'MakeDangerItem("삭제…", null,' in template_entry and
        "rename.IsEnabled = canEditTemplates;" in template_entry,
    "tray templates offer the same per-template commands under template-edit policy":
        "BuildTrayTemplateMenu(trayCursor)" in app and
        'new Forms.ToolStripMenuItem("그룹 템플릿");' in tray_templates and
        "create.Enabled = canCreate;" in tray_templates and
        "rename.Enabled = canEdit;" in tray_templates and "delete.Enabled = canEdit;" in tray_templates,
    "renaming and deleting a template check policy and deletion is confirmed":
        'EnsureManagedCapability("Templates.Edit", "그룹 템플릿 이름 바꾸기")' in rename_template and
        'EnsureManagedCapability("Templates.Edit", "그룹 템플릿 삭제")' in delete_template and
        "MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No" in delete_template,
    "the 50-template limit names the templates it would delete and asks first":
        save_template.index("if (!ConfirmTemplateLimitEviction(source, existing)) return;") <
        save_template.index("_state.Templates.Insert(0,") and
        "GroupTemplateLimitPolicy.EvictedBySave(_state.Templates, replacing)" in ops,
    # #23 "메뉴만" tab list
    "'메뉴만' tab button opens the shared tab list under the button":
        "if (Model.TabStyle == DeckTabStyle.MenuOnly)" in fence and "OpenMenuOnlyTabList(button);" in fence and
        'var source = BuildTabsMenu(CapabilityAllowed("Settings.EditGroups"));' in tab_list and
        "menu.Placement = PlacementMode.Bottom;" in tab_list,
    # #24 Peek after launch
    "opening an item ends Peek (setting, default on) and Esc in a Peek group ends it":
        launch.count("NotifyLaunched();") == 2 and
        "ShortcutLaunchService.NotifyLaunched();" in system_items and
        "ShortcutLaunchService.Launched += OnItemLaunchedEndPeek;" in ops and
        "if (_disposed || !_peek || !_state.Settings.EndPeekAfterLaunch) return;" in end_peek and
        "public bool EndPeekAfterLaunch { get; set; } = true;" in models and
        keyboard.index("DeckCutClipboardVisualState.Clear();") <
        keyboard.index("if (e.Key == Key.Escape && _peekMode && PeekEndRequested is not null)"),
    # #26 global item search
    "item search is reachable from the tray, the group menu and Ctrl+Shift+F":
        '"항목 찾기…"' in app and 'itemSearchMenuItem.Tag = "search.items";' in app and
        'case "search.items":' in routing and
        'WithGesture(MakeItem("모든 그룹에서 찾기…", () => GlobalSearchRequested?.Invoke()), "Ctrl+Shift+F")' in populate and
        keyboard.index("Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F") <
        keyboard.index("ShowSearch();"),
    "item search covers group tabs, Folder Portal folders (in the background) and icons outside groups":
        all(s in open_search for s in ("GlobalItemSearchSource.Collection,", "GlobalItemSearchSource.Desktop,",
                                       "_state.LooseDesktopItems", "_ = LoadPortalSearchEntriesAsync(window, portals);")) and
        "await Task.Run(() => EnumeratePortalSearchEntries(sources));" in ops and
        "Directory.EnumerateFileSystemEntries(source.Path).Take(PortalSearchEntryLimit)" in ops,
    "item search matches names only and shows group › tab · page":
        "name.Contains(token, StringComparison.CurrentCultureIgnoreCase)" in search_policy and
        "fileName.Contains(token, StringComparison.CurrentCultureIgnoreCase)" in search_policy and
        "entry.Path.Contains" not in search_policy and
        '" › "' in search_policy and '"바탕화면 (그룹 밖)"' in search_policy,
    "revealing a result switches page, selects tab and item and opens a rolled-up group":
        "GoToPage(window.Model.PageIndex);" in reveal and "window.RevealItem(tabId, entry.Path);" in reveal and
        "SetPeek(true);" in reveal and
        "if (Model.RolledUp) SetRollupPreview(true);" in reveal_item and
        "ItemsList.ScrollIntoView(tile);" in reveal_item and "SelectFreeTile(border, tile);" in reveal_item,
    "item search window is dark (high contrast keeps system colours)":
        "DeckThemeResources.MergeDarkControls(Resources);" in search_window and
        'DeckThemeResources.ControlStyle("DeckPrimaryButtonStyle")' in search_window and
        "SystemParameters.HighContrast" in search_window and "CreateResultItemStyle()" in search_window,
    "a Peek started by '위치로 이동' ends when another app comes to the front":
        "if (_peek) WatchRevealStartedPeek();" in reveal and
        "if (IsCyanDeckInForeground()) return;" in reveal and
        "return processId == (uint)Environment.ProcessId;" in reveal,
    "in-group search no longer matches the Desktop or Folder Portal folder part of paths":
        "GlobalItemSearchPolicy.SearchablePath(tile.Path, GetSearchExcludedRoots())" in matches_search and
        "tile.Path.Contains(q" not in matches_search and
        "SelectedTab is { Kind: DeckTabKind.FolderPortal } portal ? portal.PortalPath : null;" in fence_ops,
    "locked groups cannot be sent to another page":
        "IsEnabled = canMove && !Model.Locked && SendToPageRequested is not null" in send_menu and
        "if (model.Locked) return;" in send_page,
    "'새 페이지로 보내기' never leaves the group's old page empty behind it":
        "var newPage = NewPageTargetProvider?.Invoke(this) ?? maxPage + 1;" in send_menu and
        "window.NewPageTargetProvider = GetNewPageTargetFor;" in ops and
        "return !ownShowOnAllPages && ownPage == target ? -1 : target;" in policy and
        "GroupPageSendPolicy.NewPageTarget(2, false, 1) == -1" in harness_tests,
    "a template larger than the target monitor is shrunk to the free spot it was placed in":
        "if (fence.Width > placement.Width) fence.Width = placement.Width;" in ops and
        "if (fence.Height > placement.Height) fence.Height = placement.Height;" in ops,
    "new settings have safe defaults and the schema is unchanged":
        "public bool FollowGroupSentToPage { get; set; } = true;" in models and
        "Current = 35" in schema,
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
