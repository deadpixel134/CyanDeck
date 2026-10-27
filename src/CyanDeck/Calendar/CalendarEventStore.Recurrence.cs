namespace CyanDeck.Calendar;

// Repeat-rule storage. Every change is staged on a detached list and published
// only after the checkpoint succeeds, exactly like UpsertBatch/Delete.
public sealed partial class CalendarEventStore
{
    private static void ValidateRecurrenceDraft(CalendarEventDraft draft, CalendarEvent? existing)
    {
        if (existing?.SeriesEditPending == true)
            throw new InvalidOperationException("모든 반복 일정 변경이 아직 Google에 반영되지 않았습니다. 동기화한 뒤 다시 수정하세요.");
        if (draft.Recurrence is null)
        {
            if (existing?.Recurrence is not null && !string.IsNullOrEmpty(existing.RemoteId))
                throw new InvalidOperationException("Google 업로드가 시작된 반복 일정의 반복 규칙은 바꿀 수 없습니다. 삭제한 뒤 다시 만드세요.");
            return;
        }
        if (!CalendarRecurrenceRule.TryParse(draft.Recurrence, out var rule))
            throw new ArgumentException("반복 규칙을 이해할 수 없습니다.");
        if (existing?.IsRecurringInstance == true)
            throw new InvalidOperationException("Google 반복 일정의 개별 일정에는 반복 규칙을 지정할 수 없습니다.");
        if (existing is not null && !string.IsNullOrEmpty(existing.RemoteId)
            && !string.Equals(existing.Recurrence, draft.Recurrence, StringComparison.Ordinal))
            throw new InvalidOperationException(existing.Recurrence is null
                ? "이미 Google에 연결된 일정은 반복 일정으로 바꿀 수 없습니다. 새 일정으로 만드세요."
                : "Google 업로드가 시작된 반복 일정의 반복 규칙은 바꿀 수 없습니다. 삭제한 뒤 다시 만드세요.");
        // Google keeps the uploaded EXDATEs; moving the series dates locally would
        // bring skipped occurrences back on shifted dates once Google instances arrive.
        if (existing?.Recurrence is not null && !string.IsNullOrEmpty(existing.RemoteId)
            && draft.StartDate.Date != existing.Date.Date)
            throw new InvalidOperationException("Google에 올린 반복 일정은 날짜를 바꿀 수 없습니다. 시간·제목만 바꾸거나 동기화한 뒤 '이 일정만'을 선택하세요.");
        if (rule.Until is { } until && until.Date < draft.StartDate.Date)
            throw new ArgumentException("반복 종료 날짜는 시작 날짜 이후여야 합니다.");
        if (rule.Frequency == CalendarRecurrenceFrequency.Weekly && rule.ByDays.Count > 0
            && !rule.ByDays.Contains(draft.StartDate.DayOfWeek))
            throw new ArgumentException("매주 반복 요일에 시작 날짜의 요일을 포함하세요.");
    }

    private static void ApplyRecurrenceDraft(CalendarEvent ev, CalendarEventDraft draft, CalendarEvent? existing)
    {
        ev.Recurrence = draft.Recurrence;
        var exceptions = existing?.RecurrenceExceptions;
        if (existing is null || draft.Recurrence is null || exceptions is null || exceptions.Count == 0)
        {
            ev.RecurrenceExceptions = null;
            return;
        }
        // Moving the whole series moves its skipped dates with it.
        var shift = RecurrenceExceptionShiftDays(draft, existing);
        ev.RecurrenceExceptions = exceptions.Select(d => d.Date.AddDays(shift)).Distinct().OrderBy(d => d).ToList();
    }

    // Days by which skipped dates move. A weekly series whose weekday set was
    // changed (not merely translated) keeps its skipped dates where they are.
    internal static int RecurrenceExceptionShiftDays(CalendarEventDraft draft, CalendarEvent existing)
    {
        var shift = (draft.StartDate.Date - existing.Date.Date).Days;
        if (shift == 0) return 0;
        if (CalendarRecurrenceRule.TryParse(draft.Recurrence, out var next) && next.ByDays.Count > 0
            && CalendarRecurrenceRule.TryParse(existing.Recurrence, out var previous) && previous.ByDays.Count > 0)
        {
            var offset = ((shift % 7) + 7) % 7;
            var moved = previous.ByDays.Select(d => ((int)d + offset) % 7).OrderBy(d => d);
            if (!moved.SequenceEqual(next.ByDays.Select(d => (int)d).OrderBy(d => d))) return 0;
        }
        return shift;
    }

    // "이 일정만" on a local series: skip the occurrence date and, when a
    // replacement draft is given, add it as independent single events.
    public int SaveOccurrenceException(CalendarEvent master, DateTime occurrenceDate, CalendarEventDraft? replacement)
    {
        if (IsReadOnly) throw new IOException("Calendar data could not be loaded. See diagnostics before editing.");
        ArgumentNullException.ThrowIfNull(master);
        if (master.RemoteReadOnly) throw new InvalidOperationException("이 Google 일정은 읽기 전용입니다.");
        if (!CalendarRecurrenceRule.TryParse(master.Recurrence, out _))
            throw new InvalidOperationException("반복 일정이 아닙니다.");
        if (!string.IsNullOrEmpty(master.RemoteId))
            throw new InvalidOperationException("Google에 올리는 중인 반복 일정입니다. 동기화가 끝난 뒤 이 일정만 바꿀 수 있습니다.");
        var index = Events.FindIndex(e => ReferenceEquals(e, master));
        if (index < 0) throw new InvalidOperationException("수정하려는 일정이 현재 저장소에 없습니다.");
        var titles = Array.Empty<string>();
        if (replacement is not null)
        {
            if (replacement.Recurrence is not null)
                throw new ArgumentException("이 일정만 바꿀 때는 반복을 지정할 수 없습니다.");
            if (replacement.EndDate.Date < replacement.StartDate.Date) throw new ArgumentException("종료 날짜는 시작 날짜보다 빠를 수 없습니다.");
            if (replacement.StartAt.HasValue != replacement.EndAt.HasValue)
                throw new ArgumentException("시간 일정에는 시작·종료 시간이 모두 필요합니다.");
            if (replacement.StartAt.HasValue && replacement.EndAt <= replacement.StartAt)
                throw new ArgumentException("종료 시간은 시작 시간 이후여야 합니다.");
            if (!replacement.LocalOnly && string.IsNullOrWhiteSpace(replacement.CalendarId))
                throw new ArgumentException("저장할 캘린더를 선택하세요.");
            titles = ParseLines(replacement.Text);
            if (titles.Length == 0) throw new ArgumentException("일정 제목을 하나 이상 입력하세요.");
        }
        var staged = new List<CalendarEvent>(Events);
        var updatedMaster = Copy(master);
        var exceptions = updatedMaster.RecurrenceExceptions is null
            ? new List<DateTime>() : new List<DateTime>(updatedMaster.RecurrenceExceptions);
        if (!exceptions.Any(d => d.Date == occurrenceDate.Date)) exceptions.Add(occurrenceDate.Date);
        exceptions.Sort();
        updatedMaster.RecurrenceExceptions = exceptions;
        updatedMaster.Dirty = !updatedMaster.LocalOnly;
        staged[index] = updatedMaster;
        foreach (var title in titles)
        {
            staged.Add(new CalendarEvent
            {
                Date = replacement!.StartDate.Date, EndDate = replacement.EndDate.Date,
                StartAt = replacement.StartAt, EndAt = replacement.EndAt,
                CalendarId = replacement.LocalOnly ? LocalCalendarId : replacement.CalendarId,
                LocalOnly = replacement.LocalOnly, Title = title, Dirty = !replacement.LocalOnly
            });
        }
        SaveSnapshot(staged);
        Events = staged;
        return titles.Length;
    }

    // "모든 반복 일정" on a Google instance: keep the instance's dates, record
    // the start before the first pending change, and let the next sync PATCH
    // the series event (title and time of day).
    public void UpsertSeriesChange(CalendarEvent instance, CalendarEventDraft draft)
    {
        if (IsReadOnly) throw new IOException("Calendar data could not be loaded. See diagnostics before editing.");
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(draft);
        if (instance.RemoteReadOnly) throw new InvalidOperationException("이 Google 일정은 읽기 전용입니다.");
        if (!instance.IsRecurringInstance) throw new InvalidOperationException("Google 반복 일정이 아닙니다.");
        // An unsent "이 일정만" edit would be overwritten by the series re-pull
        // and would also give the series time shift a wrong base.
        if (instance.Dirty && !instance.SeriesEditPending)
            throw new InvalidOperationException("이 일정만 변경한 내용이 아직 동기화되지 않았습니다. 동기화한 뒤 모든 반복 일정을 바꾸세요.");
        if (draft.Recurrence is not null)
            throw new InvalidOperationException("Google 반복 일정의 반복 규칙은 Google에서 바꾸세요.");
        if (draft.StartAt.HasValue != instance.StartAt.HasValue || draft.EndAt.HasValue != instance.EndAt.HasValue)
            throw new InvalidOperationException("모든 반복 일정을 바꿀 때는 종일·시간 지정을 바꿀 수 없습니다.");
        if (draft.StartDate.Date != instance.Date.Date)
            throw new InvalidOperationException("모든 반복 일정을 바꿀 때는 날짜를 바꿀 수 없습니다. '이 일정만'을 선택하세요.");
        if (draft.StartAt.HasValue && draft.EndAt <= draft.StartAt)
            throw new ArgumentException("종료 시간은 시작 시간 이후여야 합니다.");
        var titles = ParseLines(draft.Text);
        if (titles.Length != 1) throw new ArgumentException("모든 반복 일정을 바꿀 때는 제목을 한 줄로 입력하세요.");
        var index = Events.FindIndex(e => ReferenceEquals(e, instance));
        if (index < 0) throw new InvalidOperationException("수정하려는 일정이 현재 저장소에 없습니다.");
        var staged = new List<CalendarEvent>(Events);
        var ev = Copy(instance);
        if (!ev.SeriesEditPending) ev.SeriesBaseStartAt = instance.StartAt;
        ev.SeriesEditPending = true;
        ev.Title = titles[0];
        ev.StartAt = draft.StartAt;
        ev.EndAt = draft.EndAt;
        if (draft.StartAt.HasValue) ev.EndDate = draft.EndDate.Date;
        ev.Deleted = false;
        ev.Dirty = true;
        ev.Conflict = false;
        staged[index] = ev;
        SaveSnapshot(staged);
        Events = staged;
    }

    // "모든 반복 일정" delete of a Google series. The tombstone stays on this
    // instance; the next sync deletes the series event on Google.
    public void DeleteSeries(CalendarEvent instance)
    {
        if (IsReadOnly || instance.RemoteReadOnly) throw new InvalidOperationException("이 일정은 삭제할 수 없습니다.");
        if (!instance.IsRecurringInstance) throw new InvalidOperationException("Google 반복 일정이 아닙니다.");
        var index = Events.FindIndex(item => ReferenceEquals(item, instance));
        if (index < 0) throw new InvalidOperationException("삭제하려는 일정이 현재 저장소에 없습니다.");
        var staged = new List<CalendarEvent>(Events);
        var tombstone = Copy(instance);
        tombstone.Deleted = true;
        tombstone.Dirty = true;
        tombstone.Conflict = false;
        tombstone.SeriesEditPending = true;
        staged[index] = tombstone;
        SaveSnapshot(staged);
        Events = staged;
    }
}
