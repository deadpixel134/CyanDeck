#!/usr/bin/env python3
"""v73: repeating calendar events (local RRULE series, Google instance/series edits)
and start/end time linking in the event editor. Non-destructive static checks;
the pure rules run in tests/CalendarDomainHarness."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

cal = "src/CyanDeck/Calendar/"
store = read(cal + "CalendarEventStore.cs")
store_rec = read(cal + "CalendarEventStore.Recurrence.cs")
move = read(cal + "CalendarEventMovePolicy.cs")
rule = read(cal + "CalendarRecurrence.cs")
time_policy = read(cal + "CalendarEditorTimePolicy.cs")
picker = read(cal + "CalendarTimePicker.cs")
parts = read(cal + "CalendarEventEditorParts.cs")
window = read(cal + "CalendarWindow.cs")
window_rec = read(cal + "CalendarWindow.Recurrence.cs")
sync = read(cal + "GoogleCalendarSync.cs")
sync_rec = read(cal + "GoogleCalendarSync.Recurrence.cs")
harness = read("tests/CalendarDomainHarness/Program.cs")
harness_project = read("tests/CalendarDomainHarness/CalendarDomainHarness.csproj")
schema = read("src/CyanDeck/Services/StateSchema.cs")

editor = window.split("private void BeginEditing(", 1)[1].split("private void TogglePreferences()", 1)[0]
delete_click = window.split('var delete = MakeButton("일정 삭제");', 1)[1].split("actions.Children.Add(delete);", 1)[0]
copy = store.split("private static CalendarEvent Copy(CalendarEvent source)", 1)[1].split("};", 1)[0]

NEW_FILES = (store_rec, rule, time_policy, parts, window_rec, sync_rec)
# Repository rule: no double-quoted literal inside an interpolation hole.
quote_in_hole = re.compile(r'\$"[^"\n]*\{[^}\n]*"')

checks = {
    # C4 model: optional fields with safe defaults, copied by every snapshot.
    "event model has optional recurrence fields":
        all(s in store for s in ("public string? Recurrence { get; set; }",
            "public List<DateTime>? RecurrenceExceptions { get; set; }",
            "public string? RecurringEventId { get; set; }",
            "public bool SeriesEditPending { get; set; }",
            "public DateTimeOffset? SeriesBaseStartAt { get; set; }",
            "[JsonIgnore] public bool IsRecurringInstance => !string.IsNullOrEmpty(RecurringEventId);",
            "public string? Recurrence { get; init; }")),
    "snapshot copy keeps recurrence fields (deep list copy)":
        "Recurrence = source.Recurrence," in copy and
        "RecurrenceExceptions = source.RecurrenceExceptions is null ? null : new List<DateTime>(source.RecurrenceExceptions)" in copy and
        "RecurringEventId = source.RecurringEventId," in copy and
        "SeriesEditPending = source.SeriesEditPending, SeriesBaseStartAt = source.SeriesBaseStartAt" in copy,
    "store validates and applies the repeat rule in the same checkpoint":
        "ValidateRecurrenceDraft(draft, existing);" in store and
        "ApplyRecurrenceDraft(ev, draft, updated == 0 ? existing : null);" in store and
        "public sealed partial class CalendarEventStore" in store and
        "Recurrence = existing?.Recurrence" in store,
    "moving an event keeps its repeat rule":
        "Recurrence = source.Recurrence" in move,
    "store offers this-only, whole-series and series-delete operations":
        all(s in store_rec for s in ("public int SaveOccurrenceException(", "public void UpsertSeriesChange(",
            "public void DeleteSeries(", "SaveSnapshot(staged);", "ev.SeriesBaseStartAt = instance.StartAt;",
            "tombstone.SeriesEditPending = true;")),
    "Google-linked events cannot silently change their rule":
        "이미 Google에 연결된 일정은 반복 일정으로 바꿀 수 없습니다." in store_rec and
        "Google 반복 일정의 개별 일정에는 반복 규칙을 지정할 수 없습니다." in store_rec,
    "RRULE subset: DAILY/WEEKLY/MONTHLY/YEARLY, INTERVAL, BYDAY, UNTIL or COUNT":
        all(s in rule for s in ('"DAILY" => CalendarRecurrenceFrequency.Daily', '"WEEKLY" => CalendarRecurrenceFrequency.Weekly',
            '"MONTHLY" => CalendarRecurrenceFrequency.Monthly', '"YEARLY" => CalendarRecurrenceFrequency.Yearly',
            'case "INTERVAL":', 'case "BYDAY":', 'case "UNTIL":', 'case "COUNT":',
            "if (frequency is null || (until.HasValue && count.HasValue)) return false;")),
    "expansion is pure, bounded and honours COUNT/EXDATE":
        "public static IReadOnlyList<CalendarRecurrenceOccurrence> Expand(" in rule and
        "produced++; // COUNT includes excluded dates (EXDATE applies after RRULE)." in rule and
        "private const int MaxCandidates = 20000;" in rule,
    # Display: occurrences for the visible range, no duplicates with Google instances.
    "calendar renders local series as occurrences in the visible range":
        "events = ExpandRecurringForDisplay(events, first, first.AddDays(viewport.WeekCount * 7)).ToArray();" in window and
        "CalendarRecurrenceExpander.Expand(ev, firstDay, endExclusive)" in window_rec and
        'if (IsRecurringForDisplay(bar.Event)) name = "↻ " + name;' in window,
    "an uploaded series is never shown twice (local expansion vs Google instances)":
        "if (CalendarRecurrenceExpander.IsReplacedByGoogleInstances(ev, all)) continue;" in window_rec and
        "CalendarRecurrenceExpander.RemoveReplacedMasters(pageSnapshot);" in sync and
        '["singleEvents"] = "true"' in sync,
    # Editor.
    "editor has a dark repeat combo with end by date or count":
        all(f'Content = "{t}"' in parts for t in ("반복 안 함", "매일", "매주", "매월", "매년", "없음", "날짜까지", "횟수")) and
        'ControlStyle("DarkComboBoxStyle")' in parts and
        "new CalendarRecurrenceEditor(initialRule, timeLink.StartDate, TextLight)" in editor,
    "editor asks this-only vs all for repeating events":
        'Content = "이 일정만"' in parts and 'Content = "모든 반복 일정"' in parts and
        '"반복 일정 중 이 일정만 변경됩니다."' in parts and
        "new CalendarSeriesScopeSelector(TextLight," in editor and
        "SaveRecurringEdit(draft, existing!, occurrence, scope?.AllSelected == true)" in editor,
    "read-only refusal gives the actual reason":
        "if (existing?.RemoteReadOnly == true)" in editor and "SetStatus(ReadOnlyEditMessage(existing));" in editor and
        '"반복 일정은 Google에서 주최자만 수정할 수 있습니다."' in window_rec and
        '"이 Google 캘린더에는 수정 권한이 없습니다."' in window_rec,
    "delete asks for the chosen scope before changing anything":
        "RecurringDeleteQuestion(title, hasSeriesScope, deleteWholeSeries)" in delete_click and
        "MessageBoxResult.No" in delete_click and
        delete_click.index("MessageBox.Show(") < delete_click.index("DeleteRecurring(existing, occurrence, deleteWholeSeries)")
            < delete_click.index("_store.Delete(existing)"),
    "repeating occurrences are not dragged as single events":
        "if (!CanRelocateRecurring(ev, out reason)) return false;" in window and
        '"반복 일정은 두 번 클릭해 편집 창에서 바꾸세요."' in window_rec,
    "discarding a conflict also discards a pending series change":
        "staged.SeriesEditPending = false;" in window and "staged.SeriesBaseStartAt = null;" in window,
    "unsaved-edit detection includes scope and repeat settings":
        "targets.SelectedValue, localOnly.IsChecked, scope?.AllSelected, recurrenceEditor?.Signature());" in editor,
    # Google.
    "recurring instances are editable only for the organizer copy or guestsCanModify":
        "var recurring = GoogleRecurringEditPolicy.IsReadOnlyRecurring(item);" in sync and
        '"guestsCanModify"' in rule and '"organizer"' in rule and '"self"' in rule and
        "RecurringEventId = recurringId" in sync and "local.RecurringEventId = recurringId;" in sync,
    "old read-only instances are refreshed once by a full listing":
        "if (NeedsRecurringRefresh(source.Id)) syncToken = null;" in sync and
        "GoogleRecurringEditPolicy.NeedsRecurrenceRefresh(e)" in sync_rec,
    "new series upload sends RRULE/EXDATE and an IANA time zone":
        "object insertPayload = RecurringInsertPayload(sent) ?? insert;" in sync and
        '["recurrence"] = GoogleRecurrencePayload.Lines(sent, zone, timeZoneId)' in sync_rec and
        '["timeZone"] = timeZoneId' in sync_rec and
        "TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)" in rule and
        "object patchPayload = RecurringPatchPayload(sent) ?? patch;" in sync,
    "whole-series changes and deletes go to the series event with If-Match":
        "return await PushSeriesChangeAsync(ev, calendarId);" in sync and
        "SendApiAsync(HttpMethod.Delete, seriesUrl, etag: seriesTag)" in sync_rec and
        "SendApiAsync(HttpMethod.Patch, seriesUrl, patch, seriesTag)" in sync_rec and
        "GoogleRecurringEditPolicy.ShiftSeriesTimes(seriesStart, baseStart, instanceStart, instanceEnd)" in sync_rec and
        "_preferences.UpdateSyncToken(calendarId, null);" in sync_rec,
    "a rejected series change becomes a conflict instead of stopping the sync":
        "or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)" in sync_rec and
        "MarkSeriesConflict(id);" in sync_rec,
    # A rejected series delete must not hide the series for good.
    "a rejected series delete is undone and shown with the conflict menu":
        "if (!CalendarRecurrenceExpander.UndoConflictedSeriesDelete(current)) current.Conflict = true;" in sync_rec and
        "CalendarRecurrenceExpander.RevealConflictedSeriesDeletes(pageSnapshot);" in sync and
        "all.Any(e => e.Deleted && e.SeriesEditPending && !e.Conflict" in rule and
        "if (!_preferences.Data.Catalog.Any(c => c.Id == calendarId && c.CanWrite))" in sync_rec and
        sync.find("return await PushSeriesChangeAsync(ev, calendarId);")
            < sync.find("if (!_preferences.Data.Catalog.Any(c => c.Id == calendarId && c.CanWrite))"),
    "a whole-series change never discards an unsent this-only edit":
        "if (instance.Dirty && !instance.SeriesEditPending)" in store_rec and
        "allBlockedReason: isGoogleInstance && existing.Dirty && !existing.SeriesEditPending" in editor and
        "All.IsEnabled = false;" in parts,
    "an uploaded local series keeps its dates (Google keeps its EXDATEs)":
        "&& draft.StartDate.Date != existing.Date.Date)" in store_rec and
        "if (seriesMasterLinked)" in editor and "startPicker.IsEnabled = false;" in editor,
    "whole local series date moves stay valid for weekly rules":
        "CalendarRecurrenceExpander.ResolveSeriesStart(master.Date, shift, seriesRule)" in window_rec and
        "public static DateTime ResolveSeriesStart(" in rule and
        "var shift = RecurrenceExceptionShiftDays(draft, existing);" in store_rec,
    "series colour survives Google replacement and this-only edits":
        "var appearance = AppearanceFor(bar.Event);" in window and
        "var eventStyle = AppearanceFor(ev);" in window and
        "CalendarRecurrenceExpander.SeriesLocalId(ev.RecurringEventId)" in window_rec and
        "CopySeriesAppearance(occurrence.Master.Id," in window_rec and
        'staged.RemoteId = "dd" + staged.Id.ToString("N");' in sync,
    "read-only organizer message only when the organizer rule is the cause":
        "public bool RemoteOrganizerOnly { get; set; }" in store and
        "RemoteOrganizerOnly = source.RemoteOrganizerOnly" in copy and
        "var organizerOnly = GoogleRecurringEditPolicy.IsOrganizerOnly(item, source.CanWrite);" in sync and
        "RemoteOrganizerOnly = organizerOnly" in sync and "local.RemoteOrganizerOnly = organizerOnly;" in sync and
        "if (ev.RemoteOrganizerOnly)" in window_rec and
        'reason = ev.Deleted ? "이 일정은 읽기 전용이므로 이동할 수 없습니다." : ReadOnlyEditMessage(ev);' in window,
    "existing upload filters are unchanged":
        "!x.LocalOnly && !x.Conflict && !x.RemoteReadOnly" in sync,
    # C5.
    "time picker reports user changes and supports programmatic sets":
        "public event EventHandler? TimeChanged;" in picker and "public void SetTime(TimeSpan time)" in picker and
        "TimeChanged?.Invoke(this, EventArgs.Empty);" in picker and "_suppressChange = true;" in picker,
    "minutes in 5-minute steps keep an odd minute; typed time accepted":
        "CalendarEditorTimePolicy.MinuteChoices(SelectedTime.Minutes)" in picker and
        "for (var minute = 0; minute < 60; minute++)" not in picker and
        "CalendarEditorTimePolicy.TryParseTime(_typed.Text, out var typed)" in picker and
        '"분 (5분 단위)"' in picker,
    "changing the start keeps the event length":
        "new CalendarEditorTimeLink(useDate, startPicker, endPicker, useTime, startTime, endTime," in editor and
        "CalendarEditorTimePolicy.ShiftEnd(_lastStart, _lastEnd, newStart)" in parts and
        "_endTime.SetTime(newEnd.TimeOfDay);" in parts and
        "if (duration <= TimeSpan.Zero) duration = DefaultDuration;" in time_policy,
    "end before start is prevented in the UI (next-day hint, end-date clamp)":
        "CalendarEditorTimePolicy.ResolveTimedEnd(from, startTime.SelectedTime, to, endTime.SelectedTime)" in editor and
        '"다음 날 "' in time_policy and
        '"종료 날짜는 시작 날짜보다 빠를 수 없어 시작 날짜로 맞췄습니다."' in parts,
    # Tests and hygiene.
    "domain harness compiles and exercises the new pure code":
        all(f'Include="../../src/CyanDeck/Calendar/{f}"' in harness_project
            for f in ("CalendarEventStore.Recurrence.cs", "CalendarRecurrence.cs", "CalendarEditorTimePolicy.cs")) and
        all(s in harness for s in ("weekly BYDAY with INTERVAL skips the in-between week",
            "an uploaded local series is replaced by Google instances, never shown twice",
            "only the organizer copy or guestsCanModify makes a Google instance editable",
            "an end before the start on the same day means the next day",
            "this-occurrence edit skips the date and stores an independent event",
            "older calendar files load with no recurrence",
            "a rejected series delete is undone and shown as a conflict",
            "a whole-series change waits for an unsent this-only edit",
            "an uploaded series keeps its dates; title changes still save",
            "whole-series date moves stay valid for weekly rules or are refused with a reason",
            "skipped dates move only when the whole weekly pattern is translated",
            "the organizer message is used only when the organizer rule is the cause",
            "Google instances of an uploaded series find the local series id for its colour")),
    "no quoted literal inside interpolation holes in new files":
        not any(quote_in_hole.search(t) for t in NEW_FILES),
    "state schema unchanged":
        "Current = 35" in schema,
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
