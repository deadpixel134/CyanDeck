using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CyanDeck.Calendar;
using CyanDeck.Models;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

/// <summary>
/// v8.0.44 update/backup/recovery behavior: rollback hold, update storage housekeeping,
/// undo snapshots with their own retention, verified export/import, protected start when
/// layout files are unreadable, start-up notices and background auto-save.
/// </summary>
public sealed partial class AppController
{
    // Layout files exist but could not be read: nothing is created or saved until the user picks
    // a recovery option. Implies _compatibilityReadOnly and Safe Mode.
    private bool _layoutUnreadableProtection;
    // Auto-save: only one background commit at a time; a change made meanwhile bumps the
    // generation so the completed save does not acknowledge it.
    private bool _backgroundPersistInFlight;
    private long _persistChangeGeneration;
    // After an import, snapshot preview images are not pruned for the rest of the session, so
    // restoring the "before import" recovery point within the session keeps its previews.
    private bool _snapshotPreviewPruneSuspended;

    private static Version RunningAppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    private void RegisterBackupRecoveryCommands() =>
        BackupRecoveryCommands.Register(RestoreRecoveryCheckpoint, CleanUpdateBackupsInteractive, ReleaseRollbackHoldInteractive);

    // ---- #4 rollback hold -------------------------------------------------------------

    private bool IsRollbackUndoPending() =>
        UpdateRollbackHold.IsRollbackUndo(UpdateRollbackHold.ReadHeldVersion(DataRootPaths.ActiveLocal), RunningAppVersion);

    private string BuildRollbackConfirmation(bool undo)
    {
        var current = UpdateRollbackHold.Format(RunningAppVersion);
        if (undo)
        {
            var held = UpdateRollbackHold.ReadHeldVersion(DataRootPaths.ActiveLocal);
            var heldText = held is null ? "" : UpdateRollbackHold.Format(held);
            return $"되돌리기를 취소하고 CyanDeck v{heldText}을(를) 다시 설치하시겠습니까?\n\n" +
                "자동 설치 보류도 해제됩니다. %APPDATA%의 레이아웃과 설정은 교체되지 않습니다.";
        }
        var intro = "마지막 CyanDeck 업데이트 전에 저장한 프로그램 파일로 되돌리시겠습니까? %APPDATA%의 레이아웃과 설정은 교체되지 않습니다.\n\n";
        if (!_state.Settings.InstallUpdatesAutomatically)
            return intro + $"'시작할 때 새 버전 자동 설치'가 꺼져 있어 되돌린 v{current}은(는) 자동으로 다시 설치되지 않습니다.";
        if (!RollbackTargetIgnoresHold())
            return intro + $"되돌린 v{current}은(는) 더 새 버전이 나오거나 직접 업데이트할 때까지 자동으로 다시 설치하지 않습니다.";
        if (_compatibilityReadOnly)
            return intro + $"되돌릴 버전은 자동 설치 보류를 지원하지 않아 다음 시작 때 v{current}을(를) 자동으로 다시 설치할 수 있습니다. 먼저 '시작할 때 새 버전 자동 설치'를 끄세요.";
        return intro + "되돌릴 버전은 자동 설치 보류를 지원하지 않으므로 '시작할 때 새 버전 자동 설치'를 끕니다.\n" +
            $"v{current} 이상으로 다시 업데이트하면 이 설정이 다시 켜집니다.";
    }

    // The rollback snapshot predates the hold (or its version is unknown): that build would
    // reinstall the held version through its own automatic install.
    private static bool RollbackTargetIgnoresHold() =>
        !UpdateRollbackHold.HonorsHold(UpdateRollbackHold.ReadSnapshotVersion(UpdateService.GetLatestRollbackDirectory()));

    // Called right before the rollback installer starts.
    private void ApplyRollbackHoldBeforeLaunch(bool undo)
    {
        if (undo)
        {
            UpdateRollbackHold.Clear(DataRootPaths.ActiveLocal);
            DiagnosticsLog.Info("Rollback undo requested; automatic-install hold released.");
            return;
        }
        var version = RunningAppVersion;
        if (UpdateRollbackHold.Record(DataRootPaths.ActiveLocal, version))
            DiagnosticsLog.Info($"Automatic install held for v{UpdateRollbackHold.Format(version)} after rollback request.");
        else
            DiagnosticsLog.Warn("Could not record the automatic-install hold before rollback.");
        // An older target build ignores the hold file but honours the setting.
        if (_state.Settings.InstallUpdatesAutomatically && !_compatibilityReadOnly && RollbackTargetIgnoresHold())
        {
            if (!UpdateRollbackHold.RecordAutoInstallPause(DataRootPaths.ActiveLocal))
            {
                DiagnosticsLog.Warn("Could not record the automatic-install pause marker; automatic install left unchanged.");
                return;
            }
            _state.Settings.InstallUpdatesAutomatically = false;
            _persistDirty = true;
            PersistNow(force: true, bypassDisplayTransition: true);
            DiagnosticsLog.Info("Rollback target predates the automatic-install hold; automatic install turned off until a hold-aware build starts.");
        }
    }

    // A hold-aware build is running again: undo the setting change made for an older rollback target.
    private void RestoreAutoInstallPausedByRollback()
    {
        // Safe Mode does not rewrite preferences; the marker waits for the next normal start.
        if (_safeMode || _compatibilityReadOnly || !UpdateRollbackHold.HasAutoInstallPause(DataRootPaths.ActiveLocal)) return;
        if (!_state.Settings.InstallUpdatesAutomatically)
        {
            _state.Settings.InstallUpdatesAutomatically = true;
            Persist();
        }
        UpdateRollbackHold.ClearAutoInstallPause(DataRootPaths.ActiveLocal);
        DiagnosticsLog.Info("Automatic install turned back on after the rollback-era pause.");
    }

    // Background (non-interactive) install path: true when the release must be skipped.
    private bool IsAutomaticInstallHeld(CyanDeckUpdateCheck check)
    {
        if (!UpdateRollbackHold.ShouldSkipAutomaticInstall(DataRootPaths.ActiveLocal, check.LatestVersion, out var held))
            return false;
        var heldText = held is null ? "?" : UpdateRollbackHold.Format(held);
        DiagnosticsLog.Info($"Background auto-install skipped: v{check.LatestVersion} is at or below the rolled-back v{heldText}; manual update remains available.");
        return true;
    }

    private bool ReleaseRollbackHoldInteractive()
    {
        var held = UpdateRollbackHold.ReadHeldVersion(DataRootPaths.ActiveLocal);
        if (held is null) return false;
        var heldText = UpdateRollbackHold.Format(held);
        if (System.Windows.MessageBox.Show(
                $"v{heldText} 자동 설치 보류를 해제하시겠습니까?\n\n해제하면 자동 업데이트가 켜져 있을 때 다음 시작부터 이 버전을 다시 설치할 수 있습니다.",
                "CyanDeck 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        var released = UpdateRollbackHold.Clear(DataRootPaths.ActiveLocal);
        if (released) DiagnosticsLog.Info($"Automatic-install hold for v{heldText} released by the user.");
        return released;
    }

    // ---- #33 update storage housekeeping ----------------------------------------------

    private void StartUpdateStorageMaintenance()
    {
        var version = RunningAppVersion;
        var held = UpdateRollbackHold.ReadHeldVersion(DataRootPaths.ActiveLocal);
        if (UpdateRollbackHold.IsStale(held, version))
        {
            // The held version (or newer) runs again: rollback never completed or the user updated.
            UpdateRollbackHold.Clear(DataRootPaths.ActiveLocal);
            DiagnosticsLog.Info("Stale automatic-install hold cleared at startup.");
        }
        RestoreAutoInstallPausedByRollback();
        // Safe Mode keeps side effects minimal; the next normal start does the housekeeping.
        if (_safeMode || UpdateService.HasPendingRecovery()) return;
        _ = Task.Run(() =>
        {
            var result = UpdateStorageMaintenance.Prune(DataRootPaths.ActiveLocal, version, DateTime.UtcNow);
            if (result.DeletedRollbackSnapshots + result.DeletedPackages + result.DeletedScripts > 0)
                DiagnosticsLog.Info($"Update storage housekeeping: rollback-snapshots={result.DeletedRollbackSnapshots}, packages={result.DeletedPackages}, scripts={result.DeletedScripts}, freed={result.FreedBytes / (1024 * 1024)} MB");
        });
    }

    private bool CleanUpdateBackupsInteractive()
    {
        if (UpdateService.HasPendingRecovery())
        {
            System.Windows.MessageBox.Show("완료되지 않은 업데이트 복구 기록이 있어 업데이트 백업을 정리하지 않았습니다. 먼저 업데이트 복구를 마치세요.",
                "CyanDeck 업데이트 백업 정리", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var usage = UpdateStorageMaintenance.Measure(DataRootPaths.ActiveLocal);
        if (System.Windows.MessageBox.Show(
                $"{usage.ToDisplayString()}\n\n정리하시겠습니까? 되돌리기에 쓰는 이전 버전 백업과 그다음 최신 백업 1개, 아직 설치하지 않은 최신 다운로드 파일은 남깁니다.",
                "CyanDeck 업데이트 백업 정리", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        var result = UpdateStorageMaintenance.Prune(DataRootPaths.ActiveLocal, RunningAppVersion, DateTime.UtcNow);
        var freedMegabytes = result.FreedBytes / (1024d * 1024d);
        var deleted = result.DeletedRollbackSnapshots + result.DeletedPackages + result.DeletedScripts;
        var skipReason = result.SkipReason ?? "no";
        DiagnosticsLog.Info($"Manual update storage cleanup: deleted={deleted}, freed={result.FreedBytes} bytes, skipped={skipReason}");
        var message = deleted == 0
            ? "정리할 업데이트 백업이 없습니다."
            : $"업데이트 백업 {deleted}개를 정리해 {freedMegabytes:0.#} MB를 확보했습니다.";
        System.Windows.MessageBox.Show(message, "CyanDeck 업데이트 백업 정리", MessageBoxButton.OK, MessageBoxImage.Information);
        return deleted > 0;
    }

    // ---- #19 monitor checkpoints -------------------------------------------------------

    // True when applying target would change any stored group placement.
    private static bool HasPlacementDifferences(IEnumerable<DeckFencePlacement> current, IEnumerable<DeckFencePlacement> target)
    {
        var byId = current.GroupBy(p => p.FenceId).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in target)
        {
            if (!byId.TryGetValue(p.FenceId, out var c)) return true;
            if (Math.Abs(p.X - c.X) > .5 || Math.Abs(p.Y - c.Y) > .5 ||
                Math.Abs(p.Width - c.Width) > .5 || Math.Abs(p.Height - c.Height) > .5 ||
                Math.Abs(p.ExpandedHeight - c.ExpandedHeight) > .5 ||
                p.RolledUp != c.RolledUp || p.RollupDockEdge != c.RollupDockEdge ||
                p.Locked != c.Locked || p.MoveTitlebarToBottomNearTop != c.MoveTitlebarToBottomNearTop ||
                p.PageIndex != c.PageIndex || p.SelectedTabIndex != c.SelectedTabIndex)
                return true;
        }
        return false;
    }

    // ---- #16 undo snapshots ------------------------------------------------------------

    private static bool IsUndoSnapshot(DeckLayoutSnapshot snapshot) =>
        LayoutSnapshotNamePolicy.IsUndoCheckpoint(snapshot.IsUndoCheckpoint, snapshot.IsAutomatic, snapshot.Name);

    private static string DescribeSnapshotForMenu(DeckLayoutSnapshot snapshot)
    {
        var label = LayoutSnapshotNamePolicy.Display(snapshot.Name, snapshot.IsAutomatic, snapshot.CreatedUtc.ToLocalTime());
        if (snapshot.IsAutomatic && !label.StartsWith("자동", StringComparison.Ordinal)) return "[자동] " + label;
        return label;
    }

    // Only checkpoints flagged by this build are offered for one-click undo. Legacy
    // "Before …" snapshots from older builds stay in the undo retention bucket and the gallery,
    // but can be weeks old, so they are never applied by the one-step command.
    private DeckLayoutSnapshot? FindLatestUndoCheckpoint() =>
        _state.Snapshots.Where(s => s.IsUndoCheckpoint && !s.IsAutomatic).OrderByDescending(s => s.CreatedUtc).FirstOrDefault();

    private string? DescribeLatestUndoCommand()
    {
        var undo = FindLatestUndoCheckpoint();
        if (undo is null) return null;
        var action = undo.UndoAction ?? LayoutSnapshotNamePolicy.LegacyUndoAction(undo.Name) ?? "배치 변경";
        return LayoutSnapshotNamePolicy.BuildUndoCommandLabel(action, undo.CreatedUtc.ToLocalTime(), DateTime.Now);
    }

    private bool IsLayoutUndoBlockedByDisplayTransition()
    {
        if (_displayTransition?.IsStableForUserLayoutAction != false && !_monitorContentSwapInProgress) return false;
        _trayController?.ShowBalloonTip(4000, "CyanDeck", "화면 구성이 바뀌는 중입니다. 잠시 후 다시 시도하세요.", Forms.ToolTipIcon.Info);
        return true;
    }

    private void UndoLastLayoutChange()
    {
        if (!EnsureManagedCapability("Layout.MoveResizeGroups", "마지막 배치 변경 되돌리기")) return;
        if (_safeMode || _compatibilityReadOnly) return;
        if (IsLayoutUndoBlockedByDisplayTransition()) return;
        var undo = FindLatestUndoCheckpoint();
        if (undo is null) return;
        var action = undo.UndoAction ?? LayoutSnapshotNamePolicy.LegacyUndoAction(undo.Name) ?? "배치 변경";
        var when = LayoutSnapshotNamePolicy.FormatShortDateTime(undo.CreatedUtc.ToLocalTime());
        if (System.Windows.MessageBox.Show(
                $"'{action}' 전 배치({when})로 그룹 위치·크기·페이지와 항목 위치를 되돌리시겠습니까?\n\n" +
                "지금 배치는 먼저 복구 지점으로 저장하므로 트레이 → 백업 및 복구 → 백업 / 복원 → 복구 체크포인트에서 다시 되돌릴 수 있습니다.",
                "CyanDeck 배치 되돌리기", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        // The state may have changed while the dialog was open.
        if (_disposed || _safeMode || _compatibilityReadOnly || !_state.Snapshots.Contains(undo)) return;
        if (IsLayoutUndoBlockedByDisplayTransition()) return;
        // The current arrangement is kept as a recovery point, so the undo itself is reversible.
        try
        {
            var checkpoint = _store.SaveRecoveryCheckpoint(_state, "before-layout-undo");
            if (string.IsNullOrWhiteSpace(checkpoint) || !File.Exists(checkpoint))
                throw new IOException("현재 배치를 복구 지점으로 저장하지 못했습니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Layout undo cancelled: the current layout could not be saved as a recovery checkpoint", ex);
            System.Windows.MessageBox.Show(
                $"현재 배치를 복구 지점으로 저장하지 못해 되돌리지 않았습니다.\n\n{ex.Message}",
                "CyanDeck 배치 되돌리기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _state.CurrentPage = Math.Clamp(undo.CurrentPage, 0, Math.Max(GetMaxPage(), undo.Fences.Count == 0 ? 0 : undo.Fences.Max(p => p.PageIndex)));
        ApplyPlacements(undo.Fences);
        ApplyItemPlacements(undo.Items);
        foreach (var window in _windows) window.RefreshItems();
        UpdateWindowVisibility();
        // The checkpoint is consumed, so repeating the command steps further back.
        _state.Snapshots.Remove(undo);
        try { _snapshotScreenPreviews?.Delete(undo.Id.ToString("D")); } catch { }
        Persist();
        RebuildTrayMenu();
        _trayController?.ShowBalloonTip(3500, "CyanDeck 배치 되돌리기", $"'{action}' 전 배치로 되돌렸습니다.", Forms.ToolTipIcon.Info);
        DiagnosticsLog.Info($"Undid last layout change: {action}");
    }

    // ---- #20 export / import -----------------------------------------------------------

    // Protected start keeps an empty in-memory state; exporting it could replace a good backup
    // file with an empty one.
    private bool RefuseExportWhileLayoutUnreadable()
    {
        if (!_layoutUnreadableProtection) return false;
        System.Windows.MessageBox.Show(
            "레이아웃 파일을 읽지 못한 보호 모드에서는 내보낼 레이아웃이 없습니다. 기존 백업 파일을 빈 내용으로 덮어쓰지 않도록 내보내기를 멈췄습니다.\n\n" +
            "먼저 트레이 → 백업 및 복구 → 레이아웃 파일 복구…에서 복원하거나 설정에서 백업을 가져오세요.",
            "CyanDeck 내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    // calendarEvents: local events written to the companion file next to the backup, or -1 on failure.
    private static void ShowExportCompleted(string path, int calendarEvents)
    {
        var companionName = Path.GetFileName(CalendarEventBackup.CompanionPathFor(path));
        var calendarNote = calendarEvents > 0
            ? $"Google에 올리지 않은 로컬 캘린더 일정 {calendarEvents}개는 같은 폴더의 {companionName} 파일에 함께 저장했습니다. 캘린더 설정의 '일정 가져오기…'로 되살릴 수 있습니다.\n\n"
            : calendarEvents < 0
                ? "로컬 캘린더 일정은 함께 저장하지 못했습니다. 캘린더 설정의 '로컬 일정 내보내기…'를 이용하세요.\n\n"
                : "";
        var message = $"백업을 저장했습니다.\n\n{path}\n\n{calendarNote}백업에 들어 있지 않은 항목: Google 캘린더 일정(Google에 보관됨), Google 로그인 정보\n\n저장한 폴더를 열까요?";
        if (System.Windows.MessageBox.Show(message, "CyanDeck 내보내기 완료", MessageBoxButton.YesNo,
                MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            var arguments = "/select,\"" + path + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DiagnosticsLog.Error("Could not open the exported backup folder", ex);
        }
    }

    // Reads and validates the backup before anything changes; null when it cannot be used.
    private DeckState? ReadImportCandidate(string path)
    {
        try
        {
            return _store.Import(path);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Selected backup could not be read; current layout unchanged", ex);
            System.Windows.MessageBox.Show(
                $"선택한 파일을 CyanDeck 백업으로 읽을 수 없습니다. 현재 레이아웃과 설정은 바뀌지 않았습니다.\n\n{ex.Message}",
                "CyanDeck 가져오기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private readonly record struct SnapshotMergeSummary(int FromBackup, int KeptCurrent, int OverLimit);

    private bool ConfirmImportWithSummary(DeckState imported, SnapshotMergeSummary merge)
    {
        var diff = RecoveryDiffService.Compare(_state, imported);
        var overLimitLine = merge.OverLimit == 0
            ? ""
            : $"• 보관 개수 한도를 넘는 오래된 스냅샷 {merge.OverLimit}개는 목록에서 빠지고 '백업 가져오기 전' 복구 지점에만 남습니다.\n";
        var message = "현재 CyanDeck 레이아웃과 설정을 이 백업으로 바꾸시겠습니까? 먼저 현재 상태를 복구 지점으로 저장합니다.\n\n" +
            $"{diff.ToDisplayString()}\n" +
            $"• 백업에 들어 있는 레이아웃 스냅샷: {merge.FromBackup}개\n" +
            $"• 현재 스냅샷 {merge.KeptCurrent}개는 지우지 않고 함께 보관합니다.\n" +
            overLimitLine + "\n" +
            "캘린더 일정과 Google 로그인은 백업에 들어 있지 않으며 바뀌지 않습니다.";
        return System.Windows.MessageBox.Show(message, "CyanDeck 가져오기", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Keeps the current snapshots next to the imported ones, then applies the same retention
    /// as PruneSnapshots (with the imported settings) right away, so the counts shown in the
    /// confirmation are exactly what remains and the next save does not silently drop more.
    /// Only mutates the not-yet-applied imported state; cancelling discards it.
    /// </summary>
    private SnapshotMergeSummary MergeCurrentSnapshotsInto(DeckState imported)
    {
        var fromBackupIds = imported.Snapshots.Select(s => s.Id).ToHashSet();
        var ids = new HashSet<Guid>(fromBackupIds);
        var addedCurrentIds = new HashSet<Guid>();
        foreach (var snapshot in _state.Snapshots)
            if (ids.Add(snapshot.Id))
            {
                imported.Snapshots.Add(snapshot);
                addedCurrentIds.Add(snapshot.Id);
            }
        var merged = imported.Snapshots.Count;
        imported.Snapshots = SelectRetainedSnapshots(imported.Snapshots, imported.Settings);
        return new SnapshotMergeSummary(
            imported.Snapshots.Count(s => fromBackupIds.Contains(s.Id)),
            imported.Snapshots.Count(s => addedCurrentIds.Contains(s.Id)),
            merged - imported.Snapshots.Count);
    }

    // Same buckets and limits as PruneSnapshots: user snapshots, automatic backups and undo
    // checkpoints are each kept separately; newest first.
    private static List<DeckLayoutSnapshot> SelectRetainedSnapshots(IEnumerable<DeckLayoutSnapshot> snapshots, DeckSettings settings)
    {
        var all = snapshots.ToList();
        var manualLimit = Math.Clamp(settings.MaxLayoutSnapshots, 1, 100);
        var automaticLimit = Math.Clamp(settings.AutomaticSnapshotRetentionDays, 1, 30);
        var keep = new HashSet<Guid>();
        foreach (var snapshot in all.Where(s => !s.IsAutomatic && !IsUndoSnapshot(s)).OrderByDescending(s => s.CreatedUtc).Take(manualLimit)) keep.Add(snapshot.Id);
        foreach (var snapshot in all.Where(s => s.IsAutomatic).OrderByDescending(s => s.CreatedUtc).Take(automaticLimit)) keep.Add(snapshot.Id);
        foreach (var snapshot in all.Where(s => !s.IsAutomatic && IsUndoSnapshot(s)).OrderByDescending(s => s.CreatedUtc).Take(LayoutSnapshotNamePolicy.UndoRetention)) keep.Add(snapshot.Id);
        return all.Where(s => keep.Contains(s.Id)).OrderByDescending(s => s.CreatedUtc).ToList();
    }

    private void ShowImportCompleted(bool restartRequired)
    {
        var message = restartRequired
            ? "백업을 가져왔습니다. CyanDeck을 일반 모드로 다시 시작합니다."
            : "백업을 가져왔습니다. 이전 상태는 트레이 → 백업 및 복구 → 백업 / 복원 → 복구 체크포인트에서 되돌릴 수 있습니다.";
        System.Windows.MessageBox.Show(message, "CyanDeck 가져오기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- #25 unreadable layout / start-up notices ---------------------------------------

    private void EnterLayoutLoadProtectionIfUnreadable()
    {
        if (!_store.EncounteredUnreadableState) return;
        _layoutUnreadableProtection = true;
        _compatibilityReadOnly = true;
        if (!_safeMode)
        {
            _safeMode = true;
            DiagnosticsLog.Info("Automatically entering protected Safe Mode: layout files exist but could not be read; nothing is created or saved until the user chooses.");
        }
    }

    private void OfferUnreadableLayoutRecovery()
    {
        if (!_layoutUnreadableProtection || _disposed) return;
        var candidate = _store.FindLatestRecoveryCandidate();
        var archive = _store.UnreadableArchiveFolder;
        var archiveLine = archive is null
            ? "읽지 못한 파일의 사본은 만들지 못했지만 원본은 그대로 두었습니다."
            : $"읽지 못한 파일의 사본을 보관했습니다: {archive}";
        var restoreLine = candidate is null
            ? "예: (사용할 수 있는 복구 지점이 없습니다)"
            : $"예: 최근 복구 지점({DescribeRecoverySource(candidate)})으로 복원 - 바뀌는 내용을 먼저 보여 줍니다";
        var message = "레이아웃 파일(layout.json)과 백업을 읽지 못했습니다. 디스크 오류나 백업·동기화 프로그램의 잠금 때문일 수 있습니다.\n\n" +
            "원본을 덮어쓰지 않도록 저장과 바탕화면 정리를 멈춘 안전 모드로 시작했습니다.\n" + archiveLine + "\n\n" +
            restoreLine + "\n" +
            "아니요: 새 기본 레이아웃으로 시작\n" +
            "취소: 설정 폴더를 열고 지금 상태 유지 (다시 시작하면 파일을 다시 읽어 봅니다)";
        var answer = System.Windows.MessageBox.Show(message, "CyanDeck 레이아웃을 읽지 못함",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Yes && candidate is not null) RecoverUnreadableLayoutFrom(candidate);
        else if (answer == MessageBoxResult.Yes)
            System.Windows.MessageBox.Show("사용할 수 있는 복구 지점이 없습니다. 트레이 → 백업 및 복구 → 레이아웃 파일 복구…에서 다른 방법을 선택하거나 설정에서 백업을 가져오세요.",
                "CyanDeck 복구", MessageBoxButton.OK, MessageBoxImage.Information);
        else if (answer == MessageBoxResult.No) StartFreshAfterUnreadableLayout();
        else if (answer == MessageBoxResult.Cancel) OpenSettingsFolder();
    }

    private static string DescribeRecoverySource(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var time = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        var label = string.Equals(name, "startup-baseline", StringComparison.OrdinalIgnoreCase)
            ? "이전 세션 시작 시점"
            : RecoveryCheckpointLabelPolicy.Describe(name);
        return $"{time} · {label}";
    }

    private void RecoverUnreadableLayoutFrom(string path)
    {
        DeckState target;
        try { target = _store.Import(path); }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Recovery source could not be read after unreadable layout", ex);
            System.Windows.MessageBox.Show($"복구 지점을 읽지 못했습니다.\n\n{ex.Message}", "CyanDeck 복구",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var diff = RecoveryDiffService.Compare(_state, target);
        if (System.Windows.MessageBox.Show(
                $"이 복구 지점으로 레이아웃을 복원하시겠습니까?\n{DescribeRecoverySource(path)}\n\n{diff.ToDisplayString()}\n\n복원한 뒤 CyanDeck을 일반 모드로 다시 시작합니다.",
                "CyanDeck 복구", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        CommitLayoutAfterUnreadableProtection(target, $"Restored layout from {Path.GetFileName(path)} after unreadable layout files");
    }

    private void StartFreshAfterUnreadableLayout()
    {
        var archive = _store.UnreadableArchiveFolder;
        var note = archive is null
            ? "주의: 읽지 못한 파일의 사본을 만들지 못했습니다. 새 레이아웃을 저장하면 원본 layout.json은 한 번 더 .bak으로 보관된 뒤 다음 저장에서 사라집니다."
            : $"읽지 못한 파일의 사본은 그대로 남습니다: {archive}";
        if (System.Windows.MessageBox.Show(
                $"새 기본 레이아웃으로 시작하시겠습니까? 그룹·탭·Folder Portal·규칙과 설정이 기본 구성으로 바뀝니다. 바탕화면의 실제 파일은 삭제하지 않습니다.\n\n{note}",
                "CyanDeck 새 기본 레이아웃", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        CommitLayoutAfterUnreadableProtection(_store.CreateDefaultState(), "Started a new default layout after unreadable layout files (explicit user choice)");
    }

    private void CommitLayoutAfterUnreadableProtection(DeckState target, string logMessage)
    {
        _compatibilityReadOnly = false;
        _layoutUnreadableProtection = false;
        ReplaceState(target);
        try
        {
            PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Layout still cannot be written after recovery choice; auto-save keeps retrying", ex);
            System.Windows.MessageBox.Show(
                "레이아웃 파일에 아직 쓸 수 없습니다. 다른 프로그램이 파일을 잠그고 있을 수 있습니다.\n저장을 계속 다시 시도합니다. 잠시 후 트레이에서 '일반 모드로 다시 시작'을 선택하세요.",
                "CyanDeck 복구", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DiagnosticsLog.Info(logMessage);
        Restart(false);
    }

    private void ScheduleStartupLayoutNotices()
    {
        var loadedFromBackup = _store.LoadedFromBackup && !_compatibilityReadOnly;
        // The interrupted-session tray command exists only in the normal group menu.
        var unclean = !string.IsNullOrWhiteSpace(_uncleanRecoveryPath) && !_safeMode && _state.Settings.EnableCyanDeck;
        if (!loadedFromBackup && !unclean) return;
        var archive = _store.UnreadableArchiveFolder;
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (_disposed || _trayController is null) return;
            if (loadedFromBackup)
            {
                var text = archive is null
                    ? "layout.json을 읽지 못해 백업 파일에서 불러왔습니다. 최근 변경 일부가 빠졌을 수 있습니다."
                    : "layout.json을 읽지 못해 백업 파일에서 불러왔습니다. 최근 변경 일부가 빠졌을 수 있습니다. 원본 사본은 설정 폴더의 unreadable 폴더에 있습니다.";
                _trayController.ShowBalloonTip(9000, "CyanDeck 레이아웃 복구", text, Forms.ToolTipIcon.Warning);
            }
            else
            {
                _trayController.ShowBalloonTip(9000, "CyanDeck 이전 세션 복구",
                    "지난번에 CyanDeck이 정상 종료되지 않았습니다. 트레이 → 백업 및 복구 → 이전 세션 복구 데이터 사용 가능…에서 그때 상태로 되돌릴 수 있습니다.",
                    Forms.ToolTipIcon.Info);
            }
        }));
    }

    // ---- #31 background auto-save ------------------------------------------------------

    /// <summary>
    /// Auto-save (debounce timer) path: builds the payload on the UI thread, then writes,
    /// flushes, replaces and retries on a background thread so a file locked by antivirus,
    /// backup or cloud sync never freezes the UI or the global mouse hooks. Forced saves
    /// (exit, settings, checkpoints) still use the synchronous PersistNow.
    /// </summary>
    private void PersistInBackground()
    {
        if (_disposed || _compatibilityReadOnly || !_persistDirty) return;
        if (_backgroundPersistInFlight) return; // The completion reschedules a pending change.
        if (_displayTransition?.DeferLayoutSaveIfTransitioning() == true)
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
        var generation = _persistChangeGeneration;
        PreparedLayoutSave prepared;
        try
        {
            prepared = _store.PrepareSave(_state);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            DiagnosticsLog.Error("Layout auto-save could not serialize the current state", ex);
            DeferPersistAfterFileError(ex);
            return;
        }
        _backgroundPersistInFlight = true;
        _ = CommitPreparedLayoutAsync(prepared, generation, addedAutomaticSnapshot);
    }

    private async Task CommitPreparedLayoutAsync(PreparedLayoutSave prepared, long generation, bool addedAutomaticSnapshot)
    {
        Exception? failure = null;
        try
        {
            await Task.Run(() => _store.SavePrepared(prepared)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => CompleteBackgroundPersist(failure, generation, addedAutomaticSnapshot)));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher already shut down; the synchronous exit save owns persistence.
        }
    }

    private void CompleteBackgroundPersist(Exception? failure, long generation, bool addedAutomaticSnapshot)
    {
        _backgroundPersistInFlight = false;
        if (_disposed) return;
        if (failure is not null)
        {
            DeferPersistAfterFileError(failure);
            return;
        }
        // A change made while the file was being written keeps the state dirty.
        if (generation == _persistChangeGeneration) _persistDirty = false;
        if (_persistStorageRetryCount > 0)
        {
            DiagnosticsLog.Info("Layout auto-save recovered after file contention.");
            _persistStorageRetryCount = 0;
            ProblemReportService.Reset("layout-save");
        }
        if (addedAutomaticSnapshot && _trayController is not null) RebuildTrayMenu();
        if (_persistDirty) Persist();
    }
}
