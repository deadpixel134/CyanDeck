using CyanDeck.Models;
using CyanDeck.Views;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

/// <summary>
/// Keeps icons and their group placement from disappearing or silently moving back:
/// user-placed markers, item moves between tabs/groups, rehoming after a tab or group is
/// removed, in-place renames, and skipping reconciles while a desktop folder is unreachable.
/// </summary>
public sealed partial class AppController
{
    private readonly DesktopRootShrinkGate _desktopRootShrinkGate = new();
    private bool _tabRemovalCheckpointSaved;
    // Set when a watcher reported an error or a smaller root set was not accepted yet, so the
    // watchers are rebound once the same roots are reachable again.
    private bool _desktopWatcherRebindNeeded;
    private DateTime _lastDesktopCleanupCheckpointUtc = DateTime.MinValue;
    private readonly List<PendingGroupedRename> _pendingGroupedRenames = [];
    private System.Windows.Threading.DispatcherTimer? _groupedRenameTimer;
    private static readonly TimeSpan GroupedRenameSettleDelay = TimeSpan.FromMilliseconds(1500);
    private const int MaxPendingGroupedRenames = 256;

    // An automatic reconcile waits for young renames to settle, but never longer than this in a row.
    private static readonly TimeSpan MaxRenameReconcileDeferral = TimeSpan.FromSeconds(6);
    private DateTime? _renameReconcileDeferredSinceUtc;

    private sealed class PendingGroupedRename
    {
        public string OldPath = "";
        public string NewPath = "";
        public DateTime QueuedUtc = DateTime.UtcNow;
    }

    private sealed record DesktopReconcileSnapshot(
        IReadOnlyList<string> Roots,
        IReadOnlyList<string> ReadableRoots,
        IReadOnlyList<string> FailedRoots,
        HashSet<string> Paths,
        Dictionary<string, bool> FolderReadability);

    private void WireItemPreservationEvents(FenceWindow window)
    {
        window.MoveTargetFencesProvider = () => _state.Fences;
        window.ItemsMoveRequested += OnItemsMoveRequested;
        window.ItemsReturnToAutoRequested += OnItemsReturnToAutoRequested;
        window.TabRemoving += OnTabRemoving;
        window.TabRemoved += OnTabRemoved;
    }

    private static bool SameDesktopPath(string? path, string normalized)
        => !string.IsNullOrWhiteSpace(path)
           && string.Equals(DesktopDirectoryService.Normalize(path), normalized, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<DeckTab> AllCollectionTabs()
        => _state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection);

    // ---- Moving items between tabs and groups -------------------------------------------

    /// <summary>
    /// Moves the same DeckItem (ID, display name, open count, Quick-hide flag) to another
    /// collection tab. A copy is made only when the user held Ctrl. Either way the result is
    /// marked as user-placed so the next automatic arrangement does not undo it.
    /// </summary>
    private void OnItemsMoveRequested(Guid sourceTabId, IReadOnlyList<Guid> itemIds, Guid targetTabId, bool copy)
    {
        if (sourceTabId == targetTabId || itemIds.Count == 0) return;
        var tabs = AllCollectionTabs().ToArray();
        var source = tabs.FirstOrDefault(t => t.Id == sourceTabId);
        var target = tabs.FirstOrDefault(t => t.Id == targetTabId);
        if (source is null || target is null) return;

        var ids = itemIds.Where(id => id != Guid.Empty).ToHashSet();
        var selected = source.Items.Where(i => ids.Contains(i.Id)).ToArray();
        if (selected.Length == 0) return;

        foreach (var item in selected)
        {
            var normalized = DesktopDirectoryService.Normalize(item.Path);
            var existing = target.Items.FirstOrDefault(i => SameDesktopPath(i.Path, normalized));
            if (existing is not null)
            {
                // Already there: a move just removes the duplicate from the source tab.
                existing.UserPlaced = true;
                if (!copy) source.Items.Remove(item);
                continue;
            }

            if (copy)
            {
                target.Items.Add(new DeckItem
                {
                    Path = item.Path,
                    DisplayNameOverride = item.DisplayNameOverride,
                    ExcludeFromQuickHide = item.ExcludeFromQuickHide,
                    UserPlaced = true
                });
                continue;
            }

            source.Items.Remove(item);
            item.UserPlaced = true;
            item.PinnedToDesktop = false;
            item.PositionX = null;
            item.PositionY = null;
            target.Items.Add(item);
            _state.LooseDesktopItems.RemoveAll(loose => loose.Id != item.Id && SameDesktopPath(loose.Path, normalized));
        }

        SyncLegacyUngroupedPaths();
        foreach (var window in _windows) window.RefreshItems();
        RefreshLooseDesktopIconHost();
        Persist();
    }

    /// <summary>
    /// "자동 분류에 다시 맡기기": clears the user-placed marker and lets the rules place the
    /// item right away, keeping its ID, display name and open count.
    /// </summary>
    private void OnItemsReturnToAutoRequested(Guid tabId, IReadOnlyList<Guid> itemIds)
    {
        if (_safeMode || itemIds.Count == 0) return;
        var tab = AllCollectionTabs().FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;

        var ids = itemIds.ToHashSet();
        var roots = DesktopDirectoryService.GetRoots();
        var changed = false;
        foreach (var item in tab.Items.Where(i => ids.Contains(i.Id)).ToArray())
        {
            if (item.UserPlaced)
            {
                item.UserPlaced = false;
                changed = true;
            }
            if (!DesktopDirectoryService.IsDirectChildOfAnyRoot(item.Path, roots)) continue;
            var normalized = DesktopDirectoryService.Normalize(item.Path);
            var desired = RuleEngine.PickTab(_state, normalized, includeRules: true);
            if (desired is null || ReferenceEquals(desired, tab)) continue;
            tab.Items.Remove(item);
            item.PositionX = null;
            item.PositionY = null;
            if (!desired.Items.Any(i => SameDesktopPath(i.Path, normalized))) desired.Items.Add(item);
            changed = true;
        }
        if (!changed) return;
        foreach (var window in _windows) window.RefreshItems();
        Persist();
    }

    /// <summary>The explicit rules run hands every user-placed icon back to automation.</summary>
    private void ClearUserPlacedMarkersForRulesRun()
    {
        var cleared = 0;
        foreach (var item in _state.Fences.SelectMany(f => f.Tabs).SelectMany(t => t.Items).Concat(_state.LooseDesktopItems))
        {
            if (!item.UserPlaced) continue;
            item.UserPlaced = false;
            cleared++;
        }
        if (cleared == 0) return;
        DiagnosticsLog.Info($"Rules run: {cleared} user-placed item marker(s) cleared");
        Persist();
    }

    // ---- Tab and group removal ---------------------------------------------------------

    private void OnTabRemoving(FenceWindow window, DeckTab tab)
        => _tabRemovalCheckpointSaved = SaveRecoveryCheckpointBeforeRemoval("before-remove-tab");

    private void OnTabRemoved(FenceWindow window, DeckTab tab)
    {
        var rehomed = RehomeDesktopItemsFromRemovedTabs(new[] { tab });
        Persist();
        NotifyRemovalRecoverable("탭을 제거했습니다.", rehomed, _tabRemovalCheckpointSaved);
        _tabRemovalCheckpointSaved = false;
    }

    private bool SaveRecoveryCheckpointBeforeRemoval(string reason)
    {
        try
        {
            var checkpoint = _store.SaveRecoveryCheckpoint(_state, reason);
            return !string.IsNullOrWhiteSpace(checkpoint) && File.Exists(checkpoint);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error($"Could not save recovery checkpoint ({reason})", ex);
            return false;
        }
    }

    /// <summary>
    /// Desktop files from a removed tab/group are placed again right away: into the tab the
    /// rules/categories pick, otherwise as a loose desktop icon, so nothing ends up invisible.
    /// The same DeckItem is reused so its ID, display name and open count survive.
    /// </summary>
    private int RehomeDesktopItemsFromRemovedTabs(IEnumerable<DeckTab> removedTabs)
    {
        var roots = DesktopDirectoryService.GetRoots();
        var rehomed = 0;
        foreach (var tab in removedTabs.Where(t => t.Kind == DeckTabKind.Collection))
        {
            foreach (var item in tab.Items.ToArray())
            {
                if (string.IsNullOrWhiteSpace(item.Path) || DesktopSystemItems.IsSupportedVirtualPath(item.Path)) continue;
                if (!DesktopDirectoryService.IsDirectChildOfAnyRoot(item.Path, roots)) continue;
                if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) continue;
                var normalized = DesktopDirectoryService.Normalize(item.Path);
                if (AllCollectionTabs().Any(t => t.Items.Any(i => SameDesktopPath(i.Path, normalized)))) continue;
                if (_state.LooseDesktopItems.Any(i => SameDesktopPath(i.Path, normalized))) continue;

                item.Path = normalized;
                item.PositionX = null;
                item.PositionY = null;
                item.UserPlaced = false;
                var destination = RuleEngine.PickTab(_state, normalized, includeRules: true);
                if (destination is not null)
                {
                    destination.Items.Add(item);
                }
                else
                {
                    item.PinnedToDesktop = false;
                    _state.LooseDesktopItems.Add(item);
                }
                rehomed++;
            }
        }

        if (rehomed > 0)
        {
            SyncLegacyUngroupedPaths();
            foreach (var window in _windows) window.RefreshItems();
            RefreshLooseDesktopIconHost();
            DiagnosticsLog.Info($"Rehomed {rehomed} desktop item(s) from a removed tab/group");
        }
        return rehomed;
    }

    private void NotifyRemovalRecoverable(string what, int rehomed, bool checkpointSaved)
    {
        var parts = new List<string> { what };
        if (rehomed > 0) parts.Add($"바탕화면 항목 {rehomed}개는 다른 탭이나 그룹 밖 아이콘으로 옮겼습니다.");
        if (checkpointSaved)
        {
            // Same path as the tray menu (CompactTrayMenu nests "백업 / 복원" under "백업 및 복구").
            parts.Add("되돌리려면 트레이 → 백업 및 복구 → 백업 / 복원 → 복구 체크포인트를 사용하세요.");
            RebuildTrayMenu();
        }
        if (parts.Count == 1) return;
        _trayController?.ShowBalloonTip(6000, "CyanDeck", string.Join(" ", parts), Forms.ToolTipIcon.Info);
    }

    /// <summary>A desktop file no tab can take stays visible as a loose desktop icon.</summary>
    private void KeepUnplaceableDesktopItemVisible(string normalizedPath, bool persist)
    {
        if (FindLooseDesktopItem(normalizedPath) is not null) return;
        if (!DesktopDirectoryService.IsDirectChildOfAnyRoot(normalizedPath, DesktopDirectoryService.GetRoots())) return;
        _state.LooseDesktopItems.Add(new DeckItem { Path = normalizedPath });
        SyncLegacyUngroupedPaths();
        RefreshLooseDesktopIconHost();
        if (persist) Persist();
    }

    // ---- Renames detected by the desktop watcher ---------------------------------------

    /// <summary>
    /// Records a watcher rename instead of applying it at once. Programs that save through a temp
    /// file rename the original away and put it back moments later; following the first rename
    /// would point the grouped item at a temp file that is deleted right after.
    /// </summary>
    private void QueueGroupedDesktopRename(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return;
        var oldNormalized = DesktopDirectoryService.Normalize(oldPath);
        var newNormalized = DesktopDirectoryService.Normalize(newPath);
        if (string.Equals(oldNormalized, newNormalized, StringComparison.Ordinal)) return;
        if (string.Equals(oldNormalized, newNormalized, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename: the same file either way, nothing can come back.
            RenameGroupedDesktopReferences(oldNormalized, newNormalized);
            return;
        }

        // a -> b followed by b -> c is one rename a -> c.
        foreach (var earlier in _pendingGroupedRenames)
        {
            if (string.Equals(earlier.NewPath, oldNormalized, StringComparison.OrdinalIgnoreCase))
            {
                earlier.NewPath = newNormalized;
                earlier.QueuedUtc = DateTime.UtcNow; // The chain is still moving; settle again.
            }
        }
        if (_pendingGroupedRenames.Count >= MaxPendingGroupedRenames) _pendingGroupedRenames.RemoveAt(0);
        _pendingGroupedRenames.Add(new PendingGroupedRename { OldPath = oldNormalized, NewPath = newNormalized });

        // With automatic import on, the debounced reconcile applies it first. The timer covers
        // the case where no reconcile follows.
        if (_groupedRenameTimer is null)
        {
            _groupedRenameTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, _dispatcher);
            _groupedRenameTimer.Tick += GroupedRenameTimer_Tick;
        }
        _groupedRenameTimer.Stop();
        _groupedRenameTimer.Interval = GroupedRenameSettleDelay;
        _groupedRenameTimer.Start();
    }

    /// <summary>
    /// An automatic reconcile that runs while a rename is younger than the settle delay would see
    /// the temp-file state of a program saving through a temp file (original name gone, temp name
    /// present) and repoint or drop the grouped item. It is pushed back until the renames settle,
    /// at most MaxRenameReconcileDeferral in a row. Explicit rules runs are never deferred.
    /// </summary>
    private bool DeferReconcileForUnsettledRenames(bool forceRules)
    {
        if (forceRules || _pendingGroupedRenames.Count == 0 ||
            !_desktopRuntimeStarted || !_state.Settings.AutoImportDesktop)
        {
            _renameReconcileDeferredSinceUtc = null;
            return false;
        }
        var now = DateTime.UtcNow;
        var youngest = _pendingGroupedRenames.Max(r => r.QueuedUtc);
        var age = now - youngest;
        if (age >= GroupedRenameSettleDelay)
        {
            _renameReconcileDeferredSinceUtc = null;
            return false;
        }
        _renameReconcileDeferredSinceUtc ??= now;
        if (now - _renameReconcileDeferredSinceUtc.Value > MaxRenameReconcileDeferral)
        {
            _renameReconcileDeferredSinceUtc = null;
            return false;
        }
        var waitMs = (int)Math.Ceiling((GroupedRenameSettleDelay - age).TotalMilliseconds) + 50;
        ScheduleDesktopReconcile(waitMs);
        return true;
    }

    private void GroupedRenameTimer_Tick(object? sender, EventArgs e)
    {
        _groupedRenameTimer?.Stop();
        if (_disposed) return;
        try { ApplyPendingGroupedRenames(null); }
        catch (Exception ex) { DiagnosticsLog.Error("Applying desktop renames to grouped items failed", ex); }
    }

    /// <summary>
    /// Applies recorded renames whose old name is really gone and whose new name really exists.
    /// With a reconcile snapshot the decision uses the folder listing of that pass; otherwise it
    /// checks the file system directly. Everything else leaves the item on its old path.
    /// </summary>
    private void ApplyPendingGroupedRenames(DesktopReconcileSnapshot? snapshot)
    {
        if (_pendingGroupedRenames.Count == 0) return;
        var pending = _pendingGroupedRenames.ToArray();
        _pendingGroupedRenames.Clear();
        _groupedRenameTimer?.Stop();
        foreach (var rename in pending)
        {
            if (!HasGroupedDesktopReference(rename.OldPath)) continue;
            bool oldPresent;
            bool newPresent;
            if (snapshot is not null)
            {
                // An old path whose folder could not be read counts as present: unknown is not gone.
                oldPresent = snapshot.Paths.Contains(rename.OldPath) || !CanJudgeDesktopItemMissing(rename.OldPath, snapshot);
                newPresent = snapshot.Paths.Contains(rename.NewPath);
            }
            else
            {
                oldPresent = DesktopPathExists(rename.OldPath);
                newPresent = DesktopPathExists(rename.NewPath);
            }
            if (DesktopItemPreservationPolicy.ShouldApplyGroupedRename(oldPresent, newPresent))
                RenameGroupedDesktopReferences(rename.OldPath, rename.NewPath);
            else
                DiagnosticsLog.Info("Desktop rename left grouped item unchanged: the original name came back or the new name is gone");
        }
    }

    private bool HasGroupedDesktopReference(string normalizedPath)
        => AllCollectionTabs().Any(t => t.Items.Any(i => SameDesktopPath(i.Path, normalizedPath)));

    private static bool DesktopPathExists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }

    /// <summary>
    /// Points grouped references at the new name in place, so the item keeps its tab, position,
    /// display name and open count. An established item stays in its tab even if the new name
    /// would match another rule.
    /// </summary>
    private void RenameGroupedDesktopReferences(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return;
        var oldNormalized = DesktopDirectoryService.Normalize(oldPath);
        var newNormalized = DesktopDirectoryService.Normalize(newPath);
        var nameChanged = !string.Equals(oldNormalized, newNormalized, StringComparison.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var tab in AllCollectionTabs())
        {
            for (var i = tab.Items.Count - 1; i >= 0; i--)
            {
                var item = tab.Items[i];
                if (!SameDesktopPath(item.Path, oldNormalized)) continue;
                if (nameChanged && tab.Items.Any(other => !ReferenceEquals(other, item) && SameDesktopPath(other.Path, newNormalized)))
                {
                    tab.Items.RemoveAt(i);
                    changed = true;
                    continue;
                }
                item.Path = newNormalized;
                if (DesktopItemPreservationPolicy.ShouldPinRenamedItem(item.AddedUtc, now)) item.UserPlaced = true;
                changed = true;
            }
        }
        if (!changed) return;
        foreach (var window in _windows) window.RefreshItems();
        Persist();
    }

    // ---- Temporarily unreachable desktop folders ---------------------------------------

    private static DesktopReconcileSnapshot CaptureDesktopReconcileSnapshot()
    {
        var roots = DesktopDirectoryService.GetRoots();
        var readable = new List<string>(roots.Count);
        var failed = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            string[] entries;
            try { entries = Directory.EnumerateFileSystemEntries(root).ToArray(); }
            catch (Exception ex)
            {
                // Exists but cannot be listed right now: its icons are left out of this pass.
                failed.Add(root);
                DiagnosticsLog.Warn($"Desktop folder could not be listed; its items are kept unchanged this pass: {ex.GetType().Name}");
                continue;
            }
            readable.Add(root);
            foreach (var entry in entries) paths.Add(DesktopDirectoryService.Normalize(entry));
        }
        return new DesktopReconcileSnapshot(roots, readable, failed, paths,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
    }

    private static bool CanJudgeDesktopItemMissing(string path, DesktopReconcileSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var failedParent = snapshot.FailedRoots.Any(root => DesktopDirectoryService.IsDirectChild(path, root));
        if (DesktopDirectoryService.IsDirectChildOfAnyRoot(path, snapshot.ReadableRoots))
            return DesktopItemPreservationPolicy.CanJudgeMissing(parentReadable: true, failedParent);
        string? parent;
        try { parent = Path.GetDirectoryName(DesktopDirectoryService.Normalize(path)); }
        catch { return false; }
        return DesktopItemPreservationPolicy.CanJudgeMissing(
            !string.IsNullOrWhiteSpace(parent) && IsDesktopFolderReadable(parent, snapshot.FolderReadability), failedParent);
    }

    private static int CountStaleGroupedDesktopReferences(IEnumerable<DeckTab> tabs, IReadOnlyList<string> readableRoots, HashSet<string> paths)
        => tabs.Sum(tab => tab.Items.Count(i =>
            DesktopDirectoryService.IsDirectChildOfAnyRoot(i.Path, readableRoots)
            && !paths.Contains(DesktopDirectoryService.Normalize(i.Path))));

    private void SaveCheckpointBeforeMassDesktopRemoval(int removalCount)
    {
        var now = DateTime.UtcNow;
        if (!DesktopItemPreservationPolicy.ShouldSaveDesktopCleanupCheckpoint(removalCount, _lastDesktopCleanupCheckpointUtc, now)) return;
        if (SaveRecoveryCheckpointBeforeRemoval("before-desktop-cleanup"))
        {
            _lastDesktopCleanupCheckpointUtc = now;
            DiagnosticsLog.Info($"Saved recovery checkpoint before removing {removalCount} missing desktop reference(s)");
        }
    }

    /// <summary>A skipped automatic reconcile is retried shortly so changes are not held until the next event.</summary>
    private void RetryDesktopReconcileAfterSkip()
    {
        _desktopReconcileSkipCount++;
        if (_disposed || !_desktopRuntimeStarted || !_state.Settings.AutoImportDesktop) return;
        ScheduleDesktopReconcile(3000);
    }

    private int _desktopReconcileSkipCount;

    // Tray "바탕화면 항목 지금 다시 검색": a pass skipped because a desktop folder is briefly
    // unreachable says so instead of silently doing nothing.
    private void RescanDesktopFromTray()
    {
        RefreshDesktopWatchers(force: true, reconcile: false);
        var skipsBefore = _desktopReconcileSkipCount;
        ReorganizeDesktop();
        if (_desktopReconcileSkipCount == skipsBefore) return;
        _trayController?.ShowBalloonTip(5000, "CyanDeck",
            _state.Settings.AutoImportDesktop
                ? "바탕화면 폴더 하나에 잠시 연결할 수 없어 다시 검색을 미뤘습니다. 다시 연결되면 자동으로 검색합니다."
                : "바탕화면 폴더 하나에 잠시 연결할 수 없어 다시 검색하지 않았습니다. 잠시 뒤 다시 시도하세요.",
            Forms.ToolTipIcon.Info);
    }

    /// <summary>Probes each folder once per pass; an offline network folder can block for seconds per probe.</summary>
    private static bool IsDesktopFolderReadable(string folder, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(folder, out var known)) return known;
        var readable = IsDesktopFolderReadable(folder);
        cache[folder] = readable;
        return readable;
    }

    private static bool IsDesktopFolderReadable(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch { return false; }
    }

    /// <summary>A smaller root set (a folder became unreachable) is accepted only after it persisted.</summary>
    private bool AcceptDesktopRootChange(IReadOnlyCollection<string> previousRoots, IReadOnlyCollection<string> currentRoots)
    {
        var wasPending = _desktopRootShrinkGate.IsPending;
        if (_desktopRootShrinkGate.ShouldAccept(previousRoots, currentRoots, DateTime.UtcNow))
        {
            if (DesktopItemPreservationPolicy.IsRootSetShrink(previousRoots, currentRoots))
                DiagnosticsLog.Warn($"Desktop folder stayed unavailable; watching {currentRoots.Count} folder(s). Stored icons are kept.");
            return true;
        }
        if (!wasPending)
            DiagnosticsLog.Info($"Desktop folder temporarily unavailable ({previousRoots.Count} -> {currentRoots.Count}); waiting before rebinding watchers");
        // The watcher on that folder may have failed; rebind once the same roots are back.
        _desktopWatcherRebindNeeded = true;
        return false;
    }

    private void SaveCheckpointBeforeDesktopRootMigration(IReadOnlyList<string> oldRoots, IReadOnlyList<string> newRoots,
        Dictionary<string, bool> readable)
    {
        var removals = 0;
        foreach (var item in AllCollectionTabs().SelectMany(t => t.Items).Concat(_state.LooseDesktopItems))
        {
            if (string.IsNullOrWhiteSpace(item.Path) || File.Exists(item.Path) || Directory.Exists(item.Path)) continue;
            var oldRoot = oldRoots.FirstOrDefault(root => DesktopDirectoryService.IsDirectChild(item.Path, root));
            if (oldRoot is null) continue;
            if (!readable.TryGetValue(oldRoot, out var isReadable))
                readable[oldRoot] = isReadable = IsDesktopFolderReadable(oldRoot);
            if (!isReadable) continue;
            var name = Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var found = newRoots.Any(root =>
            {
                var candidate = Path.Combine(root, name);
                return File.Exists(candidate) || Directory.Exists(candidate);
            });
            if (!found) removals++;
        }
        SaveCheckpointBeforeMassDesktopRemoval(removals);
    }
}
