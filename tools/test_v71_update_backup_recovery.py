#!/usr/bin/env python3
"""v8.0.44: update/backup/recovery fixes. A rolled-back version is not reinstalled automatically,
undo snapshots and monitor checkpoints have their own retention, export/import verify and summarize,
unreadable layout files are preserved, auto-save writes in the background, update backups are
pruned and the diagnostic bundle covers update failures. Non-destructive static checks only."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

app = read("src/CyanDeck/Services/AppController.cs")
part = read("src/CyanDeck/Services/AppController.UpdateBackupRecovery.cs")
startup = read("src/CyanDeck/App.xaml.cs")
hold = read("src/CyanDeck/Services/UpdateRollbackHold.cs")
storage = read("src/CyanDeck/Services/UpdateStorageMaintenance.cs")
retention = read("src/CyanDeck/Services/RecoveryCheckpointRetentionPolicy.cs")
labels = read("src/CyanDeck/Services/RecoveryCheckpointLabelPolicy.cs")
names = read("src/CyanDeck/Services/LayoutSnapshotNamePolicy.cs")
store = read("src/CyanDeck/Services/StateStore.cs")
models = read("src/CyanDeck/Models/Models.cs")
gallery = read("src/CyanDeck/Views/SnapshotGalleryWindow.xaml.cs")
bundle = read("src/CyanDeck/Services/DiagnosticBundleService.cs")
factory = read("src/CyanDeck/Services/FenceFactory.cs")
commands = read("src/CyanDeck/Services/BackupRecoveryCommands.cs")
routing = read("src/CyanDeck/Services/TrayMenuRoutingPolicy.cs")
fence_harness = read("tests/FenceVisibilityHarness/Program.cs")
fence_project = read("tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj")
reset_harness = read("tests/CompleteResetHarness/Program.cs")
reset_project = read("tests/CompleteResetHarness/CompleteResetHarness.csproj")
schema = read("src/CyanDeck/Services/StateSchema.cs")
group_menu = read("src/CyanDeck/Views/FenceWindow.ContextMenu.cs")
page_ops = read("src/CyanDeck/Services/AppController.GroupPageOperations.cs")

preflight = startup.split("private async Task<(bool ExitForUpdate, bool Attempted)> RunStartupUpdatePreflightAsync(", 1)[1].split("private static string? GetOptionValue(", 1)[0]
rollback = app.split("private void RollbackLastUpdate()", 1)[1].split("private void RepairDesktopAttachment()", 1)[0]
check_updates = app.split("private async Task CheckForUpdatesAsync(bool interactive)", 1)[1].split("private void RollbackLastUpdate()", 1)[0]
prune = app.split("private void PruneSnapshots()", 1)[1].split("private void StartAutomaticBackupTimer()", 1)[0]
pack = app.split("private void PackCurrentPage()", 1)[1].split("private bool EnsureAutomaticDailySnapshot()", 1)[0]
saved = app.split("private bool TryApplySavedMonitorPlacements(", 1)[1].split("private void ApplyInitialLayoutAdjustment()", 1)[0]
export = app.split("private void ExportConfiguration()", 1)[1].split("private bool ImportConfiguration()", 1)[0]
import_ = app.split("private bool ImportConfiguration()", 1)[1].split("private bool ResetAllWindowGeometryToDefaults()", 1)[0]
start = app.split("public void Start()", 1)[1].split("private void InitializeManagedDeployment()", 1)[0]
tick = app.split("private void PersistTimer_Tick(", 1)[1].split("private void DeferPersistAfterFileError(", 1)[0]
store_export = store.split("public void Export(DeckState state, string path)", 1)[1].split("// Win32 ERROR_SHARING_VIOLATION", 1)[0]
store_load = store.split("public DeckState Load(bool archiveUnreadable)", 1)[1].split("private DeckState? TryLoadWithRetry(", 1)[0]
background = part.split("private void PersistInBackground()", 1)[1]
undo_body = part.split("private void UndoLastLayoutChange()", 1)[1].split("// ---- #20 export / import", 1)[0]

checks = {
    # #4 rollback hold
    "rollback records a hold for the running version before launching the rollback installer":
        "ApplyRollbackHoldBeforeLaunch(undoRollback);" in rollback and
        rollback.index("ApplyRollbackHoldBeforeLaunch(undoRollback);") < rollback.index("UpdateService.TryLaunchRollback(") and
        "UpdateRollbackHold.Record(DataRootPaths.ActiveLocal, version)" in part,
    "start-time preflight never auto-reinstalls the rolled-back version (before staging)":
        "UpdateRollbackHold.ShouldSkipAutomaticInstall(DataRootPaths.ActiveLocal," in preflight and
        preflight.index("UpdateRollbackHold.ShouldSkipAutomaticInstall(") < preflight.index("UpdateService.StagePackageAsync(") and
        preflight.index("if (!check.UpdateAvailable)") < preflight.index("UpdateRollbackHold.ShouldSkipAutomaticInstall("),
    "background auto-install skips a held version before staging":
        "if (IsAutomaticInstallHeld(check)) return;" in check_updates and
        check_updates.index("if (IsAutomaticInstallHeld(check)) return;") < check_updates.index("var verifiedPackage = await UpdateService.StagePackageAsync(check);"),
    "a newer release releases the hold; manual install and rollback undo clear it":
        "public static bool ShouldReleaseHold(Version? held, Version latest) =>" in hold and
        "if (ShouldReleaseHold(held, latest))" in hold and
        "UpdateRollbackHold.Clear(DataRootPaths.ActiveLocal);" in check_updates and
        "Normalize(latest) <= Normalize(held)" in hold,
    "after a rollback the command is worded as undo and reinstalls the held version":
        "var undoRollback = IsRollbackUndoPending();" in rollback and
        "BuildRollbackConfirmation(undoRollback)" in rollback and
        '$"되돌리기 취소 (v{Format(held!)} 다시 설치)…"' in hold and
        '$"v{Format(held)} 자동 설치 보류 중"' in hold,
    "tray rollback command follows the hold (undo wording) and routes to recovery by ID":
        "menu.Items.Add(UpdateRollbackHold.DescribeRollbackCommand(DataRootPaths.ActiveLocal, RunningAppVersion), null," in app and
        '.Tag = "recovery.app-rollback";' in app and
        'case "recovery.app-rollback": return TrayMenuSection.Recovery;' in routing and
        'TrayMenuRoutingPolicy.Route("recovery.app-rollback", "되돌리기 취소 (v8.0.43 다시 설치)…")' in fence_harness,
    "stale hold is cleared at startup":
        "UpdateRollbackHold.IsStale(held, version)" in part and "StartUpdateStorageMaintenance();" in start,
    # Fixer: a rollback target older than the hold ignores it, so auto-install is paused and restored.
    "rolling back to a pre-hold build pauses auto-install; the next hold-aware start restores it":
        "public static readonly Version FirstHoldAwareVersion = new(8, 0, 44);" in hold and
        "FileVersionInfo.GetVersionInfo(exe)" in hold and
        "UpdateRollbackHold.RecordAutoInstallPause(DataRootPaths.ActiveLocal)" in part and
        "_state.Settings.InstallUpdatesAutomatically = false;" in part and
        "RestoreAutoInstallPausedByRollback();" in part,
    "a protected start keeps the previous session marker on exit":
        app.count("if (!_layoutUnreadableProtection) _store.EndSession();") == 2,
    # #16 undo snapshots
    "undo checkpoints are flagged and kept in their own bucket of 5":
        "snapshot.IsUndoCheckpoint = true;" in app and
        "public bool IsUndoCheckpoint { get; set; }" in models and "public string? UndoAction { get; set; }" in models and
        "public const int UndoRetention = 5;" in names and
        "!s.IsAutomatic && !IsUndoSnapshot(s)" in prune and
        ".Take(LayoutSnapshotNamePolicy.UndoRetention)" in prune,
    "undo and automatic snapshot names are Korean, legacy English names are translated":
        'SaveUndoLayoutSnapshot("Before' not in app and
        "SaveUndoLayoutSnapshot(LayoutSnapshotNamePolicy.AlignLeft);" in app and
        "LayoutSnapshotNamePolicy.BuildAutomaticName(today)" in app and
        '$"{action} 전 · {FormatShortDateTime(local)}"' in names and
        '$"자동 · {localDate.Month}월 {localDate.Day}일"' in names and
        "LayoutSnapshotNamePolicy.Display(snapshot.Name, snapshot.IsAutomatic, createdLocal)" in gallery and
        "DescribeSnapshotForMenu(local)" in app,
    "pack saves its undo checkpoint only after the monitor check":
        pack.index("if (monitors.Count == 0) return;") < pack.index("SaveUndoLayoutSnapshot(LayoutSnapshotNamePolicy.PackGroups);"),
    "tray offers one-step undo of the last arrange change":
        "if (DescribeLatestUndoCommand() is { } undoLabel)" in app and
        "QueueTrayDialog(UndoLastLayoutChange)" in app and
        'return $"마지막 배치 변경 되돌리기 ({action}, {when})";' in names and
        "LayoutSnapshotNamePolicy.BuildUndoCommandLabel(action, undo.CreatedUtc.ToLocalTime(), DateTime.Now)" in part and
        "_state.Snapshots.Remove(undo);" in part,
    "one-step undo offers only flagged checkpoints, shows the date for older ones, confirms and keeps a recovery point":
        "_state.Snapshots.Where(s => s.IsUndoCheckpoint && !s.IsAutomatic)" in part and
        ": FormatShortDateTime(createdLocal);" in names and
        undo_body.index("CyanDeck 배치 되돌리기") < undo_body.index('_store.SaveRecoveryCheckpoint(_state, "before-layout-undo");') < undo_body.index("ApplyPlacements(undo.Fences);") and
        '("before-layout-undo", "배치 변경 되돌리기 전")' in labels and
        "BuildUndoCommandLabel(LayoutSnapshotNamePolicy.SnapshotRestore, namedAt, namedAt.AddDays(3))" in fence_harness,
    "tray snapshot restore list leaves undo checkpoints out":
        "foreach (var snapshot in _state.Snapshots.Where(s => !IsUndoSnapshot(s)).Take(12))" in app,
    "save current layout asks for a name and confirms":
        'TextPrompt.Show(null!, "현재 레이아웃 저장", "스냅샷 이름",' in app and
        'QueueTrayDialog(SaveLayoutSnapshot)' in app and '"레이아웃 스냅샷 저장"' in app,
    "renamed undo snapshot becomes a user snapshot":
        "snapshot.IsUndoCheckpoint = false;" in gallery,
    "group duplicate names are Korean":
        '"그룹 사본"' in factory and '$"{source.Title} 사본"' in factory and "Group copy" not in factory,
    # #19 recovery checkpoint retention
    "recovery checkpoints are pruned per kind (monitor 5, important 20 or 30 days)":
        "public const int MonitorKeep = 5;" in retention and "public const int ImportantKeep = 20;" in retention and
        "TimeSpan.FromDays(30)" in retention and
        "RecoveryCheckpointRetentionPolicy.SelectForDeletion(" in store and
        "PruneRecoveryFiles(recoveryDir, 12)" not in store,
    "unchanged monitor placements create no recovery checkpoint":
        "var unchanged = !HasPlacementDifferences(previous, selected);" in saved and
        saved.index("SaveRecoveryCheckpoint(") < saved.index("ApplyPlacements(selected);"),
    "unclean-session checkpoint has a Korean label":
        '("unclean-session", "비정상 종료된 세션 시작 시점")' in labels,
    "tray recovery list keeps monitor checkpoints from crowding others out":
        "allRecoveryFiles.Where(IsMonitorCheckpoint).Take(2)" in app and
        "RecoveryCheckpointLabelPolicy.Describe(" in app and "ToolTipText = file.Name" in app,
    "settings can list and restore every recovery checkpoint":
        "internal static IReadOnlyList<RecoveryCheckpointEntry> List(string recoveryFolder, int max = 200)" in retention and
        "public static Func<string, bool>? RestoreRecoveryCheckpoint" in commands and
        "BackupRecoveryCommands.Register(RestoreRecoveryCheckpoint, CleanUpdateBackupsInteractive, ReleaseRollbackHoldInteractive)" in part,
    # #20 export / import
    "export writes a temporary file, verifies it, then replaces":
        "FileMode.CreateNew" in store_export and
        store_export.index("VerifyExportedBackup(temp, state);") < store_export.index("File.Replace(temp, path, null, ignoreMetadataErrors: true);") and
        "File.WriteAllText(" not in store_export,
    "export shows completion, location and what is not included":
        "ShowExportCompleted(dialog.FileName, calendarEvents);" in export and
        "백업에 들어 있지 않은 항목: Google 캘린더 일정(Google에 보관됨), Google 로그인 정보" in part and '"/select,\\""' in part,
    "export is refused while unreadable layout files are protected (empty state)":
        export.index("if (RefuseExportWhileLayoutUnreadable()) return;") < export.index("_store.Export(_state, dialog.FileName);") and
        "if (!_layoutUnreadableProtection) return false;" in part,
    "import validates the file and shows a change summary before any change":
        import_.index("ReadImportCandidate(dialog.FileName)") < import_.index("ConfirmImportWithSummary(imported, snapshotMerge)") < import_.index('_store.SaveRecoveryCheckpoint(_state, "before-import");') and
        "RecoveryDiffService.Compare(_state, imported)" in part,
    "import keeps current snapshots, applies retention before the summary and does not prune previews right after":
        import_.index("var snapshotMerge = MergeCurrentSnapshotsInto(imported);") < import_.index("ConfirmImportWithSummary(imported, snapshotMerge)") < import_.index("ReplaceState(imported);") and
        "imported.Snapshots = SelectRetainedSnapshots(imported.Snapshots, imported.Settings);" in part and
        "보관 개수 한도를 넘는 오래된 스냅샷 {merge.OverLimit}개는" in part and
        import_.index('_store.SaveRecoveryCheckpoint(_state, "before-import");') < import_.index("_snapshotPreviewPruneSuspended = true;") < import_.index("ReplaceState(imported);") and
        "if (_snapshotPreviewPruneSuspended) return;" in prune,
    # #25 unreadable layout
    "unreadable layout files are copied aside and reported, never replaced by defaults":
        "LastLoadOutcome = LayoutLoadOutcome.Unreadable;" in store_load and
        "ArchiveUnreadableLayoutFiles(includeBackup: true);" in store_load and
        "File.Copy(source, Path.Combine(folder, Path.GetFileName(source)), overwrite: true);" in store and
        "if (_state.Fences.Count == 0 && !_layoutUnreadableProtection) CreateDefaultLayout();" in start and
        "_uncleanRecoveryPath = _layoutUnreadableProtection ? null : _store.BeginSession(_state);" in start,
    "protected start is read-only safe mode and offers restore / new layout / folder":
        "_compatibilityReadOnly = true;" in part and
        "new Action(OfferUnreadableLayoutRecovery)" in start and
        "MessageBoxButton.YesNoCancel" in part and
        "_store.FindLatestRecoveryCandidate()" in part and
        "var readOnly = store.EncounteredUnsupportedState || store.EncounteredUnreadableState;" in startup,
    "a locked layout file is retried before falling back":
        "TryLoadWithRetry(_path)" in store_load and "System.Threading.Thread.Sleep(200 * attempt);" in store,
    "loading from a backup copy and an unclean previous session are announced once":
        "ScheduleStartupLayoutNotices();" in start and
        "최근 변경 일부가 빠졌을 수 있습니다." in part and
        "지난번에 CyanDeck이 정상 종료되지 않았습니다." in part,
    # #31 background auto-save
    "auto-save timer commits off the UI thread":
        "PersistInBackground();" in tick and "PersistNow();" not in tick and
        "await Task.Run(() => _store.SavePrepared(prepared)).ConfigureAwait(false);" in background and
        "prepared = _store.PrepareSave(_state);" in background,
    "stale background payload never overwrites a newer save":
        "if (prepared.Sequence <= _committedSequence) return false;" in store and
        "_persistChangeGeneration++;" in app and
        "if (generation == _persistChangeGeneration) _persistDirty = false;" in background and
        # Fixer: failed or synchronous saves also bump it, so a completion never acknowledges them.
        app.count("_persistChangeGeneration++;") >= 3,
    "background save failures keep the existing backoff and reporting":
        "DeferPersistAfterFileError(failure);" in background and
        "if (_backgroundPersistInFlight) return;" in background,
    # #33 update storage housekeeping
    "update backups keep the rollback target plus one, never while recovery is pending":
        "public const int ExtraRollbackSnapshotsToKeep = 1;" in storage and
        'Directory.EnumerateFiles(updates, "pending-*.json").Any()' in storage and
        "continue; // The rollback target is never removed." in storage and
        "if (version <= current)" in storage,
    "old installer scripts are pruned after a day and the usage is measurable":
        "public static readonly TimeSpan ScriptMaxAge = TimeSpan.FromDays(1);" in storage and
        "public static UpdateStorageUsage Measure(string localRoot)" in storage and
        "업데이트 백업 용량" in storage and
        "CleanUpdateBackupsInteractive()" in part,
    # #34 diagnostics
    "diagnostic bundle includes the redacted installer log tail":
        'WriteRedactedLog(updateLog, Path.Combine(logsDir, "update-installer.log"));' in bundle and
        "UpdateFailureDiagnostics.LogPath(DataRootPaths.ActiveLocal)" in bundle,
    "diagnostic summary lists channel, auto-install, failures, rollback and layout source":
        "Update channel: {channel}, auto-install=" in bundle and
        "recent-failed-packages={failures.RecentFailedPackages}" in bundle and
        "Update rollback: available={rollbackAvailable}" in bundle and
        "Layout loaded from: {loadOutcome}" in bundle and
        "state.Settings.EnableBetaUpdateChannel))}" in bundle and
        "layout.json" not in bundle.split("    private static string BuildSummary")[0],
    # shared
    "new tray recovery command is routed to recovery":
        '"레이아웃 파일 복구…" or' in routing,
    "pure policies are unit-tested in the existing harnesses":
        "Production/RecoveryCheckpointRetentionPolicy.cs" in fence_project and
        "Production/LayoutSnapshotNamePolicy.cs" in fence_project and
        "RecoveryCheckpointRetentionPolicy.SelectForDeletion(retentionFiles, retentionNow)" in fence_harness and
        "Production/UpdateRollbackHold.cs" in reset_project and "Production/UpdateStorageMaintenance.cs" in reset_project and
        "UpdateRollbackHold.ShouldSkipAutomaticInstall(mockLocal" in reset_harness and
        "UpdateStorageMaintenance.Prune(mockLocal" in reset_harness,
    # Cross-batch: the group menu offers the same one-step undo (policy-gated like its
    # neighbours), and removal checkpoints from the preservation batch are kept as important.
    "group menu offers the same one-step undo as the tray":
        "LatestLayoutUndoLabelProvider?.Invoke() is { } undoLabel" in group_menu and
        'undoItem.IsEnabled = CapabilityAllowed("Layout.MoveResizeGroups");' in group_menu and
        "window.LatestLayoutUndoLabelProvider = " in page_ops and
        "new Action(UndoLastLayoutChange)" in page_ops,
    "group/tab removal and desktop cleanup checkpoints use the important retention bucket":
        '"before-delete", "before-remove",' in retention and '"before-desktop-cleanup"' in retention,
    "state schema stays 35":
        "Current = 35" in schema,
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
