#!/usr/bin/env python3
"""v68: calendar sync, Google account and local event backup.
#5 one rejected Google event no longer stalls the whole sync; C1 prompt upload after local
changes with a pending-upload marker; C2 sign-in expiry is recognised and reconnect is one
click; C3 "Google 연결 해제…"; #14 background sync no longer closes the settings panel,
cancels a drag or resets scroll; #29 local event export/import and backup with the layout.
Non-destructive static checks; behaviour is covered by tests/CalendarDomainHarness."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

sync = read("src/CyanDeck/Calendar/GoogleCalendarSync.cs")
account = read("src/CyanDeck/Calendar/GoogleCalendarSync.Account.cs")
failure = read("src/CyanDeck/Calendar/GoogleSyncFailurePolicy.cs")
policies = read("src/CyanDeck/Calendar/CalendarSyncAccountPolicies.cs")
backup = read("src/CyanDeck/Calendar/CalendarEventBackup.cs")
store = read("src/CyanDeck/Calendar/CalendarEventStore.cs")
prefs_store = read("src/CyanDeck/Calendar/CalendarPreferences.cs")
styles = read("src/CyanDeck/Calendar/CalendarEventAppearanceStore.cs")
checkpoint = read("src/CyanDeck/Calendar/VerifiedCredentialCheckpoint.cs")
module = read("src/CyanDeck/Calendar/CalendarModule.cs")
module_sync = read("src/CyanDeck/Calendar/CalendarModule.SyncAccount.cs")
window = read("src/CyanDeck/Calendar/CalendarWindow.cs")
window_sync = read("src/CyanDeck/Calendar/CalendarWindow.SyncAccount.cs")
app = read("src/CyanDeck/Services/AppController.cs")
app_backup = read("src/CyanDeck/Services/AppController.CalendarBackup.cs")
harness = read("tests/CalendarDomainHarness/Program.cs")
harness_project = read("tests/CalendarDomainHarness/CalendarDomainHarness.csproj")
readme = read("README.md")

synchronize = sync.split("public async Task<string> SynchronizeAsync()", 1)[1].split("public async Task<IReadOnlyList<GoogleCalendarSource>> LoadCalendarsAsync(", 1)[0]
load = sync.split("public async Task<IReadOnlyList<GoogleCalendarSource>> LoadCalendarsAsync(", 1)[1].split("private async Task<IReadOnlyList<GoogleCalendarSource>> RefreshCalendarListCoreAsync(", 1)[0]
refresh = sync.split("private async Task RefreshIfNeededAsync(", 1)[1].split("private async Task<JsonElement> PostTokenAsync(", 1)[0]
post_token = sync.split("private async Task<JsonElement> PostTokenAsync(", 1)[1].split("private async Task<HttpResponseMessage> SendApiAsync(", 1)[0]
push = sync.split("private async Task<string> PushAsync(CalendarEvent selected)", 1)[1].split("private static bool MatchesPrivateId(", 1)[0]
pull = sync.split("private async Task<int> PullAsync()", 1)[1].split("public void Dispose()", 1)[0]
disconnect = account.split("public async Task<GoogleDisconnectResult> DisconnectAsync(", 1)[1].split("private static void DeleteAccountFile(", 1)[0]
silent = account.split("public bool CanSyncSilently", 1)[1].split("public bool HasAccountConnection", 1)[0]
module_synchronize = module.split("public async Task SynchronizeAsync()", 1)[1].split("public void Dispose()", 1)[0]
holiday = module.split("private async Task<string> RefreshHolidayFeedsAsync()", 1)[1].split("private void ApplyAutoSyncInterval()", 1)[0]
render = window.split("public void RenderMonth()", 1)[1].split("var viewport = CalendarViewportPolicy.Calculate(", 1)[0]
draw = window.split("public void RenderMonth(", 1)[1].split("private ContextMenu CreateEventAppearanceMenu(", 1)[0]
cancel = window.split("private void CancelEventMove()", 1)[1].split("public void ApplyCalendarAppearance()", 1)[0]
restore = window.split("private void RestoreRemoteEvent(CalendarEvent ev)", 1)[1].split("private static Color ParseColor(", 1)[0]
prefs = window.split("private void TogglePreferences()", 1)[1].split("private void AddPreferencesSectionTitle(", 1)[0]
export_config = app.split("private void ExportConfiguration()", 1)[1].split("private bool ImportConfiguration()", 1)[0]
complete_reset = app.split("private bool ResetAllLocalUserData()", 1)[1].split("string? ticket = null;", 1)[0]
disconnect_ui = window_sync.split("private async Task ConfirmAndDisconnectGoogleAsync(Button button)", 1)[1].split("private void AddLocalEventBackupSection(", 1)[0]
import_ui = window_sync.split("private void ImportLocalEvents()", 1)[1].split("private static string DescribeSkippedImport(", 1)[0]

checks = {
    # #5: per-event failure handling.
    "push loop isolates one rejected event and still pulls afterwards":
        "catch (GoogleEventRejectedException rejection)" in synchronize and
        "MarkUploadRejected(ev.Id, rejection.Reason);" in synchronize and
        synchronize.index("MarkUploadRejected(ev.Id, rejection.Reason);") < synchronize.index("var pulled = await PullAsync();") and
        # v36 guard intent: local-only, conflicted and read-only events are never pushed.
        "!x.LocalOnly && !x.Conflict && !x.RemoteReadOnly" in synchronize,
    "only permanent per-event replies become rejections; account-wide 403 still stops the sync":
        "statusCode is 400 or 403 or 404 or 410 or 422" in failure and
        "=> IsPermanentEventRejection(statusCode) && !IsAccountWideFailure(body);" in failure and
        '"ACCESS_TOKEN_SCOPE_INSUFFICIENT"' in failure and
        push.count("await ThrowIfEventRejectedAsync(") == 2 and
        "if (!GoogleSyncFailurePolicy.IsEventRejection(status, body)) return;" in account,
    "rejected event keeps a Korean reason and a refused deletion becomes visible again":
        '"forbiddenForNonOrganizer" => OrganizerOnlyDetail' in failure and
        'RejectionPrefix = "Google이 수정을 거부함: "' in failure and
        "current.SyncError = reason;" in account and "current.Conflict = true;" in account and
        "current.Deleted = false;" in account and
        # A refused pre-delete check of a never-confirmed create is shown again too (no hidden conflict).
        "if (current.Deleted && current.RemoteId is not null)" in account and
        "RemoteCreatePending" not in account,
    "an already-gone event on the pre-delete check just completes the local deletion":
        "if (probe.StatusCode == HttpStatusCode.Gone)" in push and
        push.index("if (probe.StatusCode == HttpStatusCode.Gone)") < push.index("await ThrowIfEventRejectedAsync(probe);"),
    "reason is persisted, copied and cleared by a new edit, retry, restore or successful upload":
        "public string? SyncError { get; set; }" in store and "SyncError = source.SyncError" in store and
        "ev.SyncError = null;" in store and "tombstone.SyncError = null;" in store and
        "current.SyncError = null;" in sync and
        "staged.SyncError = null;" in draw and "staged.SyncError = null;" in restore,
    "the conflict menu shows the reason next to the existing restore command":
        "menu.Items.Add(new MenuItem { Header = bar.Event.SyncError, IsEnabled = false });" in draw and
        '"Google 원격 버전으로 복원 (로컬 변경 취소)"' in draw and
        draw.index("Header = bar.Event.SyncError") < draw.index("Google 원격 버전으로 복원"),
    "meetings organised by someone else are read-only from download; unsent edits become conflicts":
        "var invitedCopy = GoogleSyncFailurePolicy.IsInvitedAttendeeCopy(item, source.Id);" in pull and
        "if (invitedCopy) readOnly = true;" in pull and
        "if (invitedCopy && !local.Conflict)" in pull and
        '"guestsCanModify"' in failure and '"organizer"' in failure and '"attendees"' in failure,
    "meetings downloaded before this version are rechecked by one full resync":
        "public bool InvitedMeetingsResynced { get; set; }" in prefs_store and
        "next.InvitedMeetingsResynced = true;" in account and "next.SyncTokens.Clear();" in account and
        "EnsureInvitedMeetingResync();" in synchronize and
        synchronize.index("EnsureInvitedMeetingResync();") < synchronize.index("var pulled = await PullAsync();"),
    # C1: prompt upload and pending marker.
    "local save, move and delete request a debounced upload instead of waiting for the interval":
        window.count("DescribeUploadAfterLocalChange(") >= 3 and
        'LocalChangeCommitted?.Invoke() == true ? "잠시 후 Google에 자동 반영"' in window_sync and
        "window.LocalChangeCommitted = OnLocalCalendarChange;" in module_sync and
        "if (_disposed || !_google.CanSyncSilently) return false;" in module_sync and
        "DebounceDelay = TimeSpan.FromSeconds(4)" in policies and
        # A delete leaves a Dirty tombstone: the prompt decision uses the push-loop filter, not the bar marker.
        "public static bool HasUploadWork(CalendarEvent ev)" in policies and
        "=> ev.Dirty && !ev.LocalOnly && !ev.Conflict && !ev.RemoteReadOnly;" in policies and
        "=> events.Any(HasUploadWork);" in policies,
    "prompt upload never opens a browser login and runs once more after a running sync":
        "_needsReauthorization" in silent and "_quotaBlockedUntilUtc" in silent and
        "token.ClientId" in silent and
        "if (_window.IsSynchronizing) _promptSyncAfterCurrent = true;" in module_sync and
        "AfterSynchronizeAttempt();" in module_synchronize and
        "if (connecting || editing || movingEvent || synchronizing) return CalendarPromptSyncDecision.RetryLater;" in policies,
    "event bars mark pending uploads and the tooltip shows time, calendar and sync state":
        "var name = CalendarEventStatusText.BarPrefix(bar.Event, _renderGoogleConnected) + bar.Event.Title;" in draw and
        "ToolTip = DescribeEventForTooltip(bar.Event)" in draw and
        'PendingMarker = "⇡ "' in policies and '"Google 업로드 대기"' in policies and
        '"yyyy-MM-dd HH:mm"' in policies and '"로컬 전용"' in policies,
    "without any Google connection, events show no upload marker":
        "googleConnected && IsPendingUpload(ev) ? PendingMarker" in policies and
        '"로컬 (Google 연결 전)"' in policies and
        "_renderGoogleConnected = HasGoogleAccount?.Invoke() != false;" in window_sync and
        "CalendarEventStatusText.Tooltip(ev, CalendarDisplayName(ev), _renderGoogleConnected)" in window_sync,
    # C2: sign-in expiry.
    "only invalid_grant on a refresh is treated as sign-in expiry":
        "public static bool IsReauthorizationRequired(int statusCode, string? body)" in failure and
        '"invalid_grant"' in failure and
        'grantType == "refresh_token"' in post_token and
        "throw new GoogleReauthRequiredException();" in post_token and
        "_needsReauthorization = true;" in refresh,
    "expired sign-in shows a clear Korean message and stops automatic retries":
        "Google 로그인이 만료되었습니다." in failure and
        "if (_needsReauthorization) return GoogleSyncFailurePolicy.ReauthMessage;" in synchronize and
        "catch (GoogleReauthRequiredException)" in synchronize and
        "_syncTimer.Tick += async (_, _) => await OnAutoSyncTickAsync();" in module and
        # Interval sync: no browser and no retry after expiry, but binding/quota messages still show.
        "!_google.CanSyncInBackground" in module_sync.split("private async Task OnAutoSyncTickAsync()", 1)[1] and
        "public bool CanSyncInBackground => HasSilentToken();" in account and
        "if (_disposed || _needsReauthorization) return false;" in account,
    "reconnect opens the browser on the first click and keeps the account binding check":
        "forceReauthorize || _needsReauthorization ||" in load and
        "catch (GoogleReauthRequiredException)" in load and
        load.index("catch (GoogleReauthRequiredException)") < load.index("await VerifyAccountBindingAsync(token);") and
        load.index("await VerifyAccountBindingAsync(token);") < load.index("_needsReauthorization = false;") and
        "var forceReauthorize = _connectHadFailure || GoogleNeedsReauthorization?.Invoke() == true;" in prefs and
        "signInExpired ?" in prefs,
    # C3: disconnect.
    "calendar settings offer Google disconnect with a confirmation that defaults to No":
        "AddGoogleDisconnectControls(panel);" in prefs and
        'MakeButton("Google 연결 해제…")' in window_sync and '"DeckDangerButtonStyle"' in window_sync and
        "MessageBoxResult.No" in disconnect_ui and "GoogleDisconnectPolicy.CountUnsentChanges(" in disconnect_ui and
        "if (_editing || IsSynchronizing || IsGoogleConnecting)" in disconnect_ui and
        "IsSynchronizing = true;" in disconnect_ui,
    "disconnect runs under the sync gate and clears tokens, binding, catalog and sync state":
        "if (!await _gate.WaitAsync(0, _shutdown.Token))" in disconnect and
        "finally { _gate.Release(); }" in disconnect and
        disconnect.index("_preferences.ClearGoogleAccount();") < disconnect.index("_store.CommitSnapshot(snapshot);")
            < disconnect.index("DeleteAccountFile(_tokenPath);") and
        "DeleteAccountFile(_accountPath);" in disconnect and "_credentials.Clear();" in disconnect and
        "_boundCalendarId = null;" in disconnect and
        '"https://oauth2.googleapis.com/revoke"' in account and
        "public void ClearGoogleAccount()" in prefs_store and "next.SyncTokens.Clear();" in prefs_store and
        "next.SelectedCalendarIds = null;" in prefs_store and
        "public void Clear()" in checkpoint,
    "no event keeps the old account's remote identity after disconnect":
        "var result = GoogleDisconnectPolicy.Apply(snapshot);" in disconnect and
        "ev.RemoteId = null;" in policies and "ev.RemoteETag = null;" in policies and
        "ev.RemoteCreatePending = false;" in policies and "ev.LocalOnly = true;" in policies,
    # #14: deferred refresh.
    "background sync and holiday refresh re-render only when the user is idle":
        "_window.RenderMonthWhenIdle();" in module_synchronize and "_window.RenderMonth();" not in module_synchronize and
        "_window?.RenderMonthWhenIdle();" in holiday and "_window.RenderMonth();" not in holiday and
        "private bool CanRenderNow => !_editing && !_showingPreferences && _moveCandidate is null;" in window_sync,
    "deferred refresh is applied after a drag or when the panel closes, keeping scroll":
        "BeginSyncStateRender();" in render and "_idleRenderPending = false;" in window_sync and
        "SetStatus(_status.Text[..^IdleRenderNote.Length]);" in window_sync and
        "if (_idleRenderPending) ScheduleIdleRender();" in cancel and
        "_weekScroll.ScrollToVerticalOffset(offset);" in window_sync and
        "if (_idleRenderPending && CanRenderNow) RenderPreservingScroll();" in window_sync,
    "interval sync waits for an open editor or a drag instead of interrupting it":
        "if (_window.IsEditing || _window.IsMovingEvent)" in module_sync and
        "_intervalSyncDeferred = true;" in module_sync and
        "public bool IsMovingEvent => _moveCandidate is not null;" in window_sync,
    # #29: local event backup.
    "calendar settings export and import local events with their colors":
        "AddLocalEventBackupSection(panel);" in prefs and
        'MakeButton("로컬 일정 내보내기…")' in window_sync and 'MakeButton("일정 가져오기…")' in window_sync and
        "CalendarEventBackup.Export(_store.Events, _eventAppearance.Get, dialog.FileName)" in window_sync and
        "_eventAppearance.SetMany(plan.Appearance);" in import_ui and
        "public void SetMany(IReadOnlyDictionary<Guid, CalendarEventAppearance> styles)" in styles,
    "import is confirmed, durable, local-only, duplicate-free and respects the managed policy":
        "MessageBox.Show(this," in import_ui and
        "_store.CommitSnapshot(_store.CreateSnapshot().Concat(plan.Added).ToList());" in import_ui and
        "if (CanImportCalendarEvents?.Invoke() == false)" in import_ui and
        'ManagedCapabilityAllowed?.Invoke("ImportExport.Import") != false' in module_sync and
        "_calendar.ManagedCapabilityAllowed = IsManagedCapabilityAllowed;" in app and
        "CalendarId = CalendarEventStore.LocalCalendarId," in backup and "LocalOnly = true, Dirty = false" in backup and
        "plan.SkippedLinked++" in backup and "plan.SkippedDuplicates++" in backup and
        'public string Format { get; set; } = "";' in backup and 'FormatName = "CyanDeck.CalendarEvents"' in backup,
    "never-confirmed creates are backed up and a stale companion is emptied":
        "=> !ev.Deleted && (ev.RemoteId is null || ev.RemoteCreatePending);" in backup and
        "if (File.Exists(companion)) Export(local, _ => new CalendarEventAppearance(), companion);" in backup,
    "layout/settings export also saves local events and the complete reset points to both exports":
        "_store.Export(_state, dialog.FileName);" in export_config and
        export_config.index("_store.Export(_state, dialog.FileName);") < export_config.index("ExportCalendarCompanion(dialog.FileName);") and
        "CalendarEventBackup.ExportCompanion(" in app_backup and "catch (Exception ex)" in app_backup and
        "'로컬 일정 내보내기'" in complete_reset and "'레이아웃 및 설정 내보내기'" in complete_reset and
        "필요한 자료는 먼저 다른 폴더에 내보내세요" not in complete_reset and
        "calendar-events.json" in readme and "Google 연결 해제…" in readme,
    # Merge-friendly structure and tests.
    "new logic lives in partial classes and harness-tested policy files":
        "public sealed partial class CalendarWindow : Window" in window and
        "public sealed partial class CalendarModule : IDisposable" in module and
        "public sealed partial class GoogleCalendarSync : IDisposable" in sync and
        all(f'Calendar/{name}.cs' in harness_project for name in
            ("GoogleSyncFailurePolicy", "CalendarSyncAccountPolicies", "CalendarEventBackup")) and
        all(label in harness for label in (
            '"organizer-only 403 rejects just that event with a Korean reason"',
            '"only an explicit invalid_grant requires signing in again"',
            '"prompt upload runs only with a silent token and waits for edits, drags and running syncs"',
            '"disconnect leaves no event that could be pushed to a different account"',
            '"imported events become local-only, Google copies are skipped and colors follow"',
            '"a layout export is not mistaken for a calendar backup"',
            '"a deleted Google event counts as upload work for the prompt sync"',
            '"events saved before any Google connection show no upload marker"',
            '"a create Google never confirmed is backed up as a local event"',
            '"an older companion is emptied when the new export has no local events"')),
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
