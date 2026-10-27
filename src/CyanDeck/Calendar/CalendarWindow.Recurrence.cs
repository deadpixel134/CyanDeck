using CyanDeck.Services;

namespace CyanDeck.Calendar;

// Recurring events in the calendar surface: display expansion of local series,
// edit/move rules and read-only explanations. Occurrences of a local series are
// detached clones; the map below resolves them back to their stored master.
public sealed partial class CalendarWindow
{
    private readonly Dictionary<CalendarEvent, CalendarRecurrenceOccurrence> _occurrenceOrigins = new();

    private IEnumerable<CalendarEvent> ExpandRecurringForDisplay(IEnumerable<CalendarEvent> source,
        DateTime firstDay, DateTime endExclusive)
    {
        _occurrenceOrigins.Clear();
        var all = _store.Events;
        var result = new List<CalendarEvent>();
        var pendingSeriesDelete = all.Any(e => e.Deleted && e.SeriesEditPending);
        foreach (var ev in source)
        {
            if (pendingSeriesDelete && CalendarRecurrenceExpander.IsHiddenBySeriesDelete(ev, all)) continue;
            if (ev.Recurrence is null || !CalendarRecurrenceRule.TryParse(ev.Recurrence, out _))
            {
                result.Add(ev);
                continue;
            }
            // Google instances already represent an uploaded series.
            if (CalendarRecurrenceExpander.IsReplacedByGoogleInstances(ev, all)) continue;
            foreach (var occurrence in CalendarRecurrenceExpander.Expand(ev, firstDay, endExclusive))
            {
                result.Add(occurrence.Event);
                _occurrenceOrigins[occurrence.Event] = occurrence;
            }
        }
        return result;
    }

    private bool IsRecurringForDisplay(CalendarEvent ev)
        => ev.IsRecurringInstance || _occurrenceOrigins.ContainsKey(ev);

    private CalendarRecurrenceOccurrence? ResolveOccurrence(CalendarEvent? ev)
    {
        if (ev is null) return null;
        // The stored master may have been replaced by a newer snapshot, or the
        // month re-rendered after the clone was handed out.
        var master = _store.Events.FirstOrDefault(e => e.Id == ev.Id && !e.Deleted);
        if (master is null || ReferenceEquals(master, ev) || master.Recurrence is null) return null;
        return _occurrenceOrigins.TryGetValue(ev, out var occurrence)
            ? occurrence with { Master = master }
            : new CalendarRecurrenceOccurrence(ev, master, ev.Date.Date);
    }

    // Says why an event cannot be edited instead of a generic refusal.
    private string ReadOnlyEditMessage(CalendarEvent ev)
    {
        if (KoreanHolidayCalendars.Contains(ev.CalendarId))
            return "기본 제공 공휴일 캘린더는 읽기 전용입니다. 설정에서 표시 여부를 변경할 수 있습니다.";
        var source = _preferences.Data.Catalog.FirstOrDefault(c => c.Id == ev.CalendarId);
        if (source is not null && !source.CanWrite)
            return "이 Google 캘린더에는 수정 권한이 없습니다.";
        // Only when the organizer rule is the recorded cause; birthdays, focus
        // time or locked events fall through to the general message.
        if (ev.RemoteOrganizerOnly)
            return "반복 일정은 Google에서 주최자만 수정할 수 있습니다.";
        return "Google에서 편집이 제한된 일정입니다.";
    }

    // Appearance is keyed by event id. Google instances of a series uploaded from
    // CyanDeck fall back to the colour of the local series until they get their own.
    private CalendarEventAppearance AppearanceFor(CalendarEvent ev)
    {
        var own = _eventAppearance.Get(ev.Id);
        if (own.BackgroundColor is not null || own.TextColor is not null) return own;
        return CalendarRecurrenceExpander.SeriesLocalId(ev.RecurringEventId) is { } masterId
            ? _eventAppearance.Get(masterId) : own;
    }

    // Detached "이 일정만" events keep the series colour. A failure here never
    // undoes the saved event; it only leaves the default colour.
    private void CopySeriesAppearance(Guid masterId, IEnumerable<Guid> targets)
    {
        var style = _eventAppearance.Get(masterId);
        if ((style.BackgroundColor is null && style.TextColor is null) || _eventAppearance.IsReadOnly) return;
        try
        {
            foreach (var id in targets) _eventAppearance.Set(id, style.BackgroundColor, style.TextColor);
        }
        catch (Exception ex) { DiagnosticsLog.Warn("Series colour was not copied to a detached event: " + ex.Message); }
    }

    private bool CanRelocateRecurring(CalendarEvent ev, out string reason)
    {
        reason = "";
        if (_occurrenceOrigins.ContainsKey(ev))
        {
            reason = "반복 일정은 두 번 클릭해 편집 창에서 바꾸세요.";
            return false;
        }
        if (ev.SeriesEditPending)
        {
            reason = "모든 반복 일정 변경이 동기화된 뒤 옮길 수 있습니다.";
            return false;
        }
        return true;
    }

    private static string RecurringDeleteQuestion(string title, bool repeating, bool wholeSeries)
        => !repeating ? $"'{title}' 일정을 삭제하시겠습니까?"
            : wholeSeries ? $"'{title}' 반복 일정을 모두 삭제하시겠습니까?"
            : $"'{title}' 반복 일정 중 이 일정만 삭제하시겠습니까?";

    private static CalendarEventDraft WithRecurrence(CalendarEventDraft draft, string? recurrence) => new()
    {
        StartDate = draft.StartDate, EndDate = draft.EndDate, StartAt = draft.StartAt, EndAt = draft.EndAt,
        CalendarId = draft.CalendarId, LocalOnly = draft.LocalOnly, Text = draft.Text, Recurrence = recurrence
    };

    // Saves an edit of a repeating event. Returns the status text, or null when
    // the ordinary single-event save applies ("이 일정만" on a Google instance,
    // which is an instance PATCH on the next sync).
    private string? SaveRecurringEdit(CalendarEventDraft draft, CalendarEvent existing,
        CalendarRecurrenceOccurrence? occurrence, bool wholeSeries)
    {
        if (occurrence is null)
        {
            if (!wholeSeries) return null;
            _store.UpsertSeriesChange(existing, WithRecurrence(draft, null));
            return "모든 반복 일정 변경 저장 · 동기화를 누르면 Google 반복 일정에 반영됩니다.";
        }
        if (!wholeSeries)
        {
            var before = _store.Events.Select(e => e.Id).ToHashSet();
            var detached = _store.SaveOccurrenceException(occurrence.Master, occurrence.OccurrenceDate,
                WithRecurrence(draft, null));
            CopySeriesAppearance(occurrence.Master.Id, _store.Events.Select(e => e.Id).Where(id => !before.Contains(id)).ToList());
            return $"반복 일정 중 이 일정만 변경 · 별도 일정 {detached}건 저장";
        }
        // Whole local series: the edited occurrence's date shift moves the series
        // start by the same number of days; times and length come from the editor.
        var master = occurrence.Master;
        var shift = (draft.StartDate.Date - occurrence.OccurrenceDate.Date).Days;
        CalendarRecurrenceRule.TryParse(draft.Recurrence, out var seriesRule);
        var seriesStart = CalendarRecurrenceExpander.ResolveSeriesStart(master.Date, shift, seriesRule);
        DateTime seriesEnd;
        DateTimeOffset? startAt = null, endAt = null;
        if (draft.StartAt is { } start && draft.EndAt is { } end)
        {
            var seriesStartAt = AtLocal(seriesStart, start.ToLocalTime().TimeOfDay);
            startAt = seriesStartAt;
            endAt = seriesStartAt + (end - start);
            seriesEnd = endAt.Value.ToLocalTime().Date;
        }
        else seriesEnd = seriesStart.AddDays(Math.Max(0, (draft.EndDate.Date - draft.StartDate.Date).Days));
        var count = _store.UpsertBatch(new CalendarEventDraft
        {
            StartDate = seriesStart, EndDate = seriesEnd, StartAt = startAt, EndAt = endAt,
            CalendarId = draft.CalendarId, LocalOnly = draft.LocalOnly, Text = draft.Text,
            Recurrence = draft.Recurrence
        }, master);
        return draft.Recurrence is null
            ? $"반복을 해제하고 일정 {count}건 저장"
            : $"모든 반복 일정 저장 · {count}건";
    }

    // Deletes according to the chosen scope. Returns false when the ordinary
    // single-event delete applies ("이 일정만" on a Google instance).
    private bool DeleteRecurring(CalendarEvent existing, CalendarRecurrenceOccurrence? occurrence, bool wholeSeries)
    {
        if (occurrence is null && !wholeSeries) return false;
        try
        {
            string message;
            if (occurrence is not null && !wholeSeries)
            {
                _store.SaveOccurrenceException(occurrence.Master, occurrence.OccurrenceDate, null);
                message = "반복 일정 중 이 일정만 삭제 완료";
            }
            else if (occurrence is not null)
            {
                _store.Delete(occurrence.Master);
                message = "반복 일정 모두 삭제 완료";
            }
            else
            {
                _store.DeleteSeries(existing);
                message = "반복 일정 모두 삭제 예약 · 동기화하면 Google에서도 삭제됩니다.";
            }
            ProblemReportService.Reset("calendar-save");
            _editing = false;
            SetStatus(message);
            RenderMonth();
        }
        catch (Exception ex) { ReportRecurringStoreFailure("삭제", ex); }
        return true;
    }

    // Shared save/delete failure reporting for the series paths.
    private void ReportRecurringStoreFailure(string action, Exception ex)
    {
        SetStatus(action + " 오류: " + ex.Message);
        if (ex is IOException or UnauthorizedAccessException)
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck", "calendar-events.json");
            ProblemReportService.ShowOnce("calendar-save", "캘린더 반복 일정 " + action, "calendar-events.json", ex,
                "반복 일정의 로컬 저장에 실패했습니다. 기존 일정은 보존됩니다.", path, path + ".tmp");
        }
    }
}
