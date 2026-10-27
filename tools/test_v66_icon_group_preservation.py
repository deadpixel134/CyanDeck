#!/usr/bin/env python3
"""Icons and groups must not vanish or move back on their own: user-placed marker,
same-item moves between tabs/groups, confirmed tab removal with rehoming, unreachable
desktop folders, and in-place renames. Non-destructive static checks; the pure policy
is exercised by the compiled FenceVisibilityHarness and the UI is verified on Windows by hand."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")
def between(text, start, end): return text.split(start, 1)[1].split(end, 1)[0]

models = read("src/CyanDeck/Models/Models.cs")
factory = read("src/CyanDeck/Services/FenceFactory.cs")
app = read("src/CyanDeck/Services/AppController.cs")
lifecycle = read("src/CyanDeck/Services/AppController.FenceWindowLifecycle.cs")
pres = read("src/CyanDeck/Services/AppController.ItemPreservation.cs")
policy = read("src/CyanDeck/Services/DesktopItemPreservationPolicy.cs")
labels = read("src/CyanDeck/Services/RecoveryCheckpointLabelPolicy.cs")
fence = read("src/CyanDeck/Views/FenceWindow.xaml.cs")
fence_pres = read("src/CyanDeck/Views/FenceWindow.ItemPreservation.cs")
group_menu = read("src/CyanDeck/Views/FenceWindow.ContextMenu.cs")
harness = read("tests/FenceVisibilityHarness/Program.cs")
harness_project = read("tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj")

reorganize = between(app, "private void ReorganizeDesktop(bool forceRules = false)", "private HashSet<string> BuildEligibleDefaultPlacementTargetKeys()")
loop = reorganize.split("foreach (var path in paths)", 1)[1]
apply_rules = between(app, "private void ApplyRulesNow()", "private void ConfigureSnapshotScreenPreviews()")
delete_fence = between(app, "private void OnDeleteFence(FenceWindow window)", "private void RefreshTab(Guid tabId)")
import_path = between(app, "private void ImportPath(string path, bool persist = true)", "private void RemovePath(string path)")
renamed = between(app, "RenamedEventHandler renamed = (_, e) =>", "watcher.Created += created;")
watcher_error = between(app, "watcher.Error += (_, e) => QueueIfCurrent(() =>", "return watcher;")
apply_renames = between(pres, "private void ApplyPendingGroupedRenames(DesktopReconcileSnapshot? snapshot)", "private bool HasGroupedDesktopReference(")
queue_rename = between(pres, "private void QueueGroupedDesktopRename(string oldPath, string newPath)", "private void GroupedRenameTimer_Tick(")
mass_checkpoint = between(pres, "private void SaveCheckpointBeforeMassDesktopRemoval(int removalCount)", "private void RetryDesktopReconcileAfterSkip()")
accept_roots = between(pres, "private bool AcceptDesktopRootChange(", "private void SaveCheckpointBeforeDesktopRootMigration(")
refresh_watchers = between(app, "private void RefreshDesktopWatchers(bool force = false, bool reconcile = true)", "private void ScheduleDesktopReconcile(")
migrate = between(app, "private void MigrateDesktopRootReferences(", "private void StartShellRecovery()")
flick = between(app, "private bool MoveLooseDesktopItemToCollection(", "private void AutoClassifyLooseItems(")
auto_classify = app.split("private void AutoClassifyLooseItems(", 1)[1].split("\n    private ", 1)[0]
move_items = between(pres, "private void OnItemsMoveRequested(", "private void OnItemsReturnToAutoRequested(")
return_auto = between(pres, "private void OnItemsReturnToAutoRequested(", "private void ClearUserPlacedMarkersForRulesRun()")
rehome = between(pres, "private int RehomeDesktopItemsFromRemovedTabs(", "private void NotifyRemovalRecoverable(")
rename_refs = between(pres, "private void RenameGroupedDesktopReferences(", "private static DesktopReconcileSnapshot CaptureDesktopReconcileSnapshot()")
snapshot = between(pres, "private static DesktopReconcileSnapshot CaptureDesktopReconcileSnapshot()", "private static bool CanJudgeDesktopItemMissing(")
remove_tab = between(fence, "private void RemoveTabById(Guid tabId)", "private void SelectTab(DeckTab tab)")
tab_drop = between(fence, "private void Tab_Drop(object sender, DragEventArgs e)", "private bool TryHandleTabDrop(")
window_drop = between(fence, "private void Window_Drop(object sender, DragEventArgs e)", "private void Header_Drop(")
dropped = between(fence, "private void AddDroppedPaths(", "private void ImportIntoPortal(")
free_down = between(fence, "private void FreeTile_MouseLeftButtonDown(", "private void FreeTile_MouseMove(")
free_move = between(fence, "private void FreeTile_MouseMove(", "private void FreeTile_MouseLeftButtonUp(")
free_up = between(fence, "private void FreeTile_MouseLeftButtonUp(", "private void FreeTile_MouseRightButtonDown(")
item_drop = between(fence_pres, "private bool TryHandleItemMoveDrop(", "private void BeginFreeGroupDrag(")
confirm_tab = fence_pres.split("private bool ConfirmRemoveTab(DeckTab tab)", 1)[1]
file_rename = between(fence_pres, "private void RenameActualDesktopFile(ItemTile tile)", "private bool ConfirmRemoveTab(DeckTab tab)")

checks = {
    # 1: user-placed marker
    "DeckItem persists a user-placed marker with a safe default and the schema stays 35":
        "public bool UserPlaced { get; set; }" in models and "public int Version { get; set; } = 35;" in models,
    "group duplicates keep the marker":
        "UserPlaced = source.UserPlaced" in factory,
    "automatic arrangement keeps user-placed (or, with maintenance off, any grouped) items":
        "DesktopItemPreservationPolicy.KeepsExistingGroupPlacement(" in loop and
        "matches.Any(m => m.Item.UserPlaced)" in loop and
        loop.index("KeepsExistingGroupPlacement(") < loop.index("if (!forceRules && _state.Settings.UseDefaultDesktopPlacement)") and
        "=> isGrouped && !forceRules && (anyUserPlaced || !useRules);" in policy,
    "only the explicit rules run clears every marker before reclassifying":
        "ClearUserPlacedMarkersForRulesRun();" in apply_rules and
        apply_rules.index("ClearUserPlacedMarkersForRulesRun();") < apply_rules.index("ReorganizeDesktop(forceRules: true);"),
    "drops, flicks and menu moves mark the item; auto-classify unmarks it":
        "tab.Items.Add(new DeckItem { Path = path });" in dropped and "tab.Items[^1].UserPlaced = true;" in dropped and
        "item.UserPlaced = true;" in flick and "item.UserPlaced = false;" in auto_classify and
        "item.UserPlaced = true;" in move_items,
    "item menu offers 자동 분류에 다시 맡기기 and applies the rules to that item right away":
        'MakeItem("자동 분류에 다시 맡기기"' in fence_pres and
        "window.ItemsReturnToAutoRequested += OnItemsReturnToAutoRequested;" in pres and
        "WireItemPreservationEvents(window);" in lifecycle and
        "item.UserPlaced = false;" in return_auto and "RuleEngine.PickTab(_state, normalized, includeRules: true)" in return_auto,
    # 7: moves keep the same item
    "item drags carry the item ids next to FileDrop; Explorer still gets copy/link only":
        'private const string ItemMoveDragFormat = "CyanDeck.Items";' in fence_pres and
        "AttachItemMovePayload(data, ItemsList.SelectedItems.Cast<ItemTile>());" in fence and
        "AttachItemMovePayload(data, FreeDragPayloadTiles(source));" in fence and
        "DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy | DragDropEffects.Link)" in fence and
        "DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Link)" in fence,
    "tab-header and body drops move the same item before the FileDrop fallback":
        "if (TryHandleItemMoveDrop(e, tab, selectTarget: true)) return;" in tab_drop and
        tab_drop.index("TryHandleItemMoveDrop(") < tab_drop.index("DataFormats.FileDrop") and
        "if (TryHandleItemMoveDrop(e, SelectedTab, selectTarget: false)) return;" in window_drop and
        window_drop.index("TryHandleItemMoveDrop(") < window_drop.index("DataFormats.FileDrop"),
    "a copy is made only with Ctrl; a move reuses the DeckItem with its name and count":
        "var copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;" in item_drop and
        "source.Items.Remove(item);" in move_items and "target.Items.Add(item);" in move_items and
        "if (copy)" in move_items,
    "free layout drags every selected icon by the same distance":
        "BeginFreeGroupDrag(tile);" in free_down and
        "(dx, dy) = ClampFreeGroupDragOffset(dx, dy);" in free_move and "MoveFreeGroupDrag(dx, dy);" in free_move and
        "CommitFreeGroupDrag();" in free_up,
    "single and multi-selection menus offer 다른 그룹/탭으로 이동":
        'new MenuItem { Header = "다른 그룹/탭으로 이동" }' in fence_pres and
        "AddItemPlacementMenuEntries(menu, tiles);" in fence and
        "AddItemPlacementMenuEntries(menu, new[] { tile });" in fence,
    # 2: tab/group removal
    "every tab removal path confirms first and defaults to No":
        "if (!ConfirmRemoveTab(removedTab)) return;" in remove_tab and
        remove_tab.index("ConfirmRemoveTab(") < remove_tab.index("Model.Tabs.RemoveAt(current);") and
        "MessageBoxResult.No" in confirm_tab and "그룹 밖 바탕화면 아이콘으로 남습니다" in confirm_tab,
    "tab removal saves a checkpoint before and rehomes desktop files after":
        remove_tab.index("TabRemoving?.Invoke(this, removedTab);") < remove_tab.index("Model.Tabs.RemoveAt(current);") <
        remove_tab.index("TabRemoved?.Invoke(this, removedTab);") and
        'SaveRecoveryCheckpointBeforeRemoval("before-remove-tab")' in pres and
        "RehomeDesktopItemsFromRemovedTabs(new[] { tab })" in pres,
    "tab removal menu items are marked dangerous":
        "removeTabItem.Tag = MenuDangerTag;" in group_menu and "remove.Tag = MenuDangerTag;" in fence,
    "group deletion saves a checkpoint, then rehomes desktop files":
        delete_fence.index('SaveRecoveryCheckpointBeforeRemoval("before-delete-group")') <
        delete_fence.index("RemoveFenceWindow(window);") < delete_fence.index("RehomeDesktopItemsFromRemovedTabs(removedTabs)"),
    "a desktop file without a destination tab stays visible as a loose icon":
        "_state.LooseDesktopItems.Add(item);" in rehome and
        "if (matches.Length == 0) changed |= EnsureLooseDesktopReference(path, matches, looseByPath);" in loop and
        "KeepUnplaceableDesktopItemVisible(full, persist);" in import_path,
    "new checkpoints have Korean labels":
        all(s in labels for s in ('("before-delete-group", "그룹 삭제 전")', '("before-remove-tab", "탭 제거 전")',
                                  '("before-desktop-cleanup", "바탕화면 항목 정리 전")')),
    # 8: unreachable desktop folders
    "automatic reconcile is skipped only while the shrink gate is actively watching an unreachable folder":
        "DesktopItemPreservationPolicy.ShouldSkipReconcile(forceRules," in reorganize and
        "_desktopRootShrinkGate.IsActivelyPending(DateTime.UtcNow), _desktopWatchers.GetRoots(), desktopSnapshot.Roots)" in reorganize and
        "RetryDesktopReconcileAfterSkip();" in reorganize and
        reorganize.index("ShouldSkipReconcile(") < reorganize.index("_state.LooseDesktopItems.Remove(") and
        "=> !forceRules && shrinkActivelyPending && IsRootSetShrink(boundRoots, currentRoots);" in policy and
        "internal bool IsActivelyPending(DateTime nowUtc)" in policy,
    "only folders read in this pass can prove an item is gone; folder probes are cached per pass":
        "var roots = desktopSnapshot.ReadableRoots;" in reorganize and
        "CanJudgeDesktopItemMissing(path, desktopSnapshot)" in reorganize and
        "failed.Add(root);" in snapshot and "RuleEngine.EnumerateDesktopItems()" not in reorganize and
        "IsDesktopFolderReadable(parent, snapshot.FolderReadability)" in pres,
    "a smaller root set is accepted only after it persisted":
        "if (!AcceptDesktopRootChange(previousRoots, roots)) return;" in refresh_watchers and
        refresh_watchers.index("AcceptDesktopRootChange(") < refresh_watchers.index("_desktopWatchers.Rebind(") and
        "internal const int MinimumObservations = 3;" in policy and
        "MinimumDuration = TimeSpan.FromSeconds(20)" in policy,
    "a failed or held-back watcher is rebound once the same roots are back":
        "if (!force && !_desktopWatcherRebindNeeded && _desktopWatchers.Matches(roots)) return;" in refresh_watchers and
        "_desktopWatcherRebindNeeded = false;" in refresh_watchers and
        refresh_watchers.index("_desktopWatchers.Rebind(") < refresh_watchers.index("_desktopWatcherRebindNeeded = false;") and
        "_desktopWatcherRebindNeeded = true;" in watcher_error and
        "_desktopWatcherRebindNeeded = true;" in accept_roots,
    "root migration removes a reference only when the old folder is readable":
        migrate.count("else if (IsDesktopFolderReadable(oldRoot, oldRootReadable))") == 2 and
        "SaveCheckpointBeforeDesktopRootMigration(oldRoots, newRoots, oldRootReadable);" in migrate,
    "mass removal saves a checkpoint first, at most once an hour":
        "SaveCheckpointBeforeMassDesktopRemoval(" in reorganize and
        "internal const int MassRemovalCheckpointThreshold = 10;" in policy and
        "DesktopItemPreservationPolicy.ShouldSaveDesktopCleanupCheckpoint(removalCount, _lastDesktopCleanupCheckpointUtc, now)" in mass_checkpoint and
        "DesktopCleanupCheckpointInterval = TimeSpan.FromHours(1)" in policy,
    "desktop folder changes wait for display transitions to settle":
        "if ((_state.Settings.TrackDesktopLocationChanges || _desktopWatcherRebindNeeded) && !displayTransitionInProgress)" in app,
    # 11: renames
    "watcher renames are queued and applied in place only when the old name is really gone":
        "QueueGroupedDesktopRename(e.OldFullPath, e.FullPath);" in renamed and
        "RenameGroupedDesktopReferences(" not in renamed and
        renamed.index("QueueGroupedDesktopRename(") < renamed.index("ScheduleDesktopReconcile()") and
        "ApplyPendingGroupedRenames(desktopSnapshot);" in reorganize and
        reorganize.index("ShouldSkipReconcile(") < reorganize.index("ApplyPendingGroupedRenames(desktopSnapshot);") <
        reorganize.index("_state.LooseDesktopItems.Remove(") and
        "_groupedRenameTimer.Start();" in queue_rename and
        "DesktopItemPreservationPolicy.ShouldApplyGroupedRename(oldPresent, newPresent)" in apply_renames and
        "RenameGroupedDesktopReferences(rename.OldPath, rename.NewPath);" in apply_renames and
        "=> !oldPathPresent && newPathPresent;" in policy and
        "item.Path = newNormalized;" in rename_refs and
        "DesktopItemPreservationPolicy.ShouldPinRenamedItem(item.AddedUtc, now)" in rename_refs,
    "desktop files get a real rename next to the display-name-only rename":
        '"표시 이름만 바꾸기…"' in fence and "AddFileRenameMenuEntry(menu, tile);" in fence and
        'MakeItem("파일 이름 바꾸기…"' in fence_pres and
        "WindowsFileNamePolicy.GetProblem(newName)" in file_rename and
        "DesktopRecycleService.IsActualDesktopItem(source)" in file_rename and
        "File.Move(trimmed, destination)" in file_rename and "tile.Source.Path = DesktopDirectoryService.Normalize(destination);" in file_rename,
    "file rename keeps hidden shortcut extensions and explains failures in Korean":
        "var hiddenExtension = HiddenShortcutExtension(trimmed);" in file_rename and
        "newName += hiddenExtension;" in file_rename and
        "UnauthorizedAccessException =>" in file_rename and "ex.Message" not in file_rename,
    "Ctrl over CyanDeck items shows the copy cursor":
        "else if (IsItemCopyDrag(e)) e.Effects = DragDropEffects.Copy;" in fence and
        "=> e.Data.GetDataPresent(ItemMoveDragFormat) && (e.KeyStates & DragDropKeyStates.ControlKey) != 0;" in fence_pres,
    # Harness
    "preservation policy is exercised by the compiled harness":
        'Include="../../src/CyanDeck/Services/DesktopItemPreservationPolicy.cs"' in harness_project and
        "DesktopItemPreservationPolicy.KeepsExistingGroupPlacement(" in harness and
        "new DesktopRootShrinkGate()" in harness and
        "DesktopItemPreservationPolicy.ShouldApplyGroupedRename(" in harness and
        "shrinkGate.IsActivelyPending(" in harness and
        "DesktopItemPreservationPolicy.ShouldSaveDesktopCleanupCheckpoint(" in harness,
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
