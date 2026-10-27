using System.Text.Json;
using System.Text.Json.Serialization;
using CyanDeck.Services;

namespace CyanDeck.Calendar;

// Keep the original Date property for backward-compatible reading of calendar-events.json.
// EndDate is INCLUSIVE in the application; Google all-day end.date is EXCLUSIVE.
public sealed class CalendarEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Date { get; set; } = DateTime.Today;
    public DateTime? EndDate { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public string Title { get; set; } = "";
    public string? CalendarId { get; set; }
    public string? RemoteId { get; set; }
    public bool RemoteCreatePending { get; set; }
    public string? RemoteETag { get; set; }
    public bool Dirty { get; set; } = true;
    public bool Deleted { get; set; }
    public bool Conflict { get; set; }
    public bool RemoteReadOnly { get; set; }
    // Explicit opt-out of Google upload, independent of Google calendar selection.
    public bool LocalOnly { get; set; }
    // Why Google refused the last upload of this event (shown in the ⚠ menu). Null when absent.
    public string? SyncError { get; set; }
    // Repeat rule (RFC 5545 RRULE subset without the "RRULE:" prefix) for a
    // CyanDeck-created series. Absent in older files = not recurring.
    public string? Recurrence { get; set; }
    // Occurrence dates removed from a local series ("이 일정만" edit/delete).
    public List<DateTime>? RecurrenceExceptions { get; set; }
    // Google series id of a pulled instance (singleEvents). An empty string
    // marks an instance-style id that was checked and is not recurring.
    public string? RecurringEventId { get; set; }
    // A pending "모든 반복 일정" change (or delete when Deleted) of a Google
    // series, sent to the series event instead of this instance.
    public bool SeriesEditPending { get; set; }
    public DateTimeOffset? SeriesBaseStartAt { get; set; }
    // Read-only because another organizer owns the recurring series (not a
    // missing calendar permission, special event type or lock). Absent = false.
    public bool RemoteOrganizerOnly { get; set; }

    [JsonIgnore] public bool IsRecurringInstance => !string.IsNullOrEmpty(RecurringEventId);
    [JsonIgnore] public bool IsAllDay => !StartAt.HasValue;
    [JsonIgnore] public DateTime EffectiveEndDate =>
        EndAt is { } end ? end.ToLocalTime().AddTicks(-1).Date : (EndDate?.Date ?? Date.Date);
}

public sealed class CalendarEventDraft
{
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? EndAt { get; init; }
    public string CalendarId { get; init; } = "primary";
    public bool LocalOnly { get; init; }
    public string Text { get; init; } = "";
    public string? Recurrence { get; init; }
}

public sealed partial class CalendarEventStore
{
    private readonly string _path;
    public List<CalendarEvent> Events { get; private set; } = [];
    public bool IsReadOnly { get; private set; }
    // Recovery is intentionally display-only. Never overwrite a damaged primary
    // file with an older .bak snapshot without an explicit user decision.
    public bool IsRecoveredFromBackup { get; private set; }

    // A custom location also allows isolated failure-injection tests without
    // accessing the user's actual calendar data.
    public CalendarEventStore(string? storagePath = null)
    {
        _path = storagePath ?? Path.Combine(DataRootPaths.ActiveRoaming, "calendar-events.json");
        try
        {
            if (File.Exists(_path))
                Events = ReadEvents(_path);
            else if (File.Exists(_path + ".bak"))
            {
                // A missing primary and a surviving backup may indicate an
                // interrupted external restore. Do not silently create a new file.
                Events = ReadEvents(_path + ".bak");
                IsReadOnly = true;
                IsRecoveredFromBackup = true;
                DiagnosticsLog.Warn("Calendar primary missing; displaying backup in read-only mode");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Cannot read calendar events; existing file left intact", ex);
            IsReadOnly = true;
            try
            {
                if (File.Exists(_path + ".bak"))
                {
                    Events = ReadEvents(_path + ".bak");
                    IsRecoveredFromBackup = true;
                    DiagnosticsLog.Warn("Calendar backup loaded for display only; primary was not overwritten");
                }
            }
            catch (Exception backupError)
            {
                DiagnosticsLog.Error("Calendar backup could not be read either", backupError);
            }
        }
    }

    // Must be called only following an explicit user confirmation. This does
    // not rewrite the .bak file and does not silently discard the bad primary.
    public string? RestoreFromBackup()
    {
        if (!IsRecoveredFromBackup || !IsReadOnly)
            throw new InvalidOperationException("복구 가능한 읽기 전용 일정 백업이 없습니다.");
        var (restored, archived) = CalendarBackupRestorer.Restore(_path, ReadEvents);
        Events = restored;
        IsRecoveredFromBackup = false;
        IsReadOnly = false;
        return archived;
    }

    private static List<CalendarEvent> ReadEvents(string path)
    {
        var result = JsonSerializer.Deserialize<List<CalendarEvent>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Calendar events JSON contains null rather than an event array.");
        if (result.Any(e => e is null))
            throw new InvalidDataException("Calendar events JSON contains a null event.");
        return result;
    }

    // Legacy single-line caller retained for compatibility.
    public void Upsert(DateTime date, string title, CalendarEvent? existing = null)
        => UpsertBatch(new CalendarEventDraft
        {
            StartDate = date.Date, EndDate = date.Date, Text = title,
            CalendarId = existing?.CalendarId ?? "primary", LocalOnly = existing?.LocalOnly == true,
            Recurrence = existing?.Recurrence
        }, existing);

    public const string LocalCalendarId = "cyandeck:local";

    public static string[] ParseLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // One input line = one independent event. Persist the whole edit in one checkpoint.
    public int UpsertBatch(CalendarEventDraft draft, CalendarEvent? existing = null)
    {
        if (IsReadOnly) throw new IOException("Calendar data could not be loaded. See diagnostics before editing.");
        if (existing?.RemoteReadOnly == true) throw new InvalidOperationException("이 Google 일정은 읽기 전용입니다.");
        if (draft.EndDate.Date < draft.StartDate.Date) throw new ArgumentException("종료 날짜는 시작 날짜보다 빠를 수 없습니다.");
        if (draft.StartAt.HasValue != draft.EndAt.HasValue)
            throw new ArgumentException("시간 일정에는 시작·종료 시간이 모두 필요합니다.");
        if (draft.StartAt.HasValue && draft.EndAt <= draft.StartAt)
            throw new ArgumentException("종료 시간은 시작 시간 이후여야 합니다.");
        if (!draft.LocalOnly && string.IsNullOrWhiteSpace(draft.CalendarId)) throw new ArgumentException("저장할 캘린더를 선택하세요.");
        if (existing is not null && existing.RemoteId is not null && draft.LocalOnly)
            throw new InvalidOperationException("이미 Google에 연결된 일정은 로컬 전용으로 변경할 수 없습니다.");
        if (existing is not null && !string.IsNullOrEmpty(existing.RemoteId)
            && !string.Equals(existing.CalendarId ?? "primary", draft.CalendarId, StringComparison.Ordinal))
            throw new InvalidOperationException("연결된 Google 일정의 캘린더 이동은 지원하지 않습니다. 기존 캘린더에 저장하세요.");
        ValidateRecurrenceDraft(draft, existing);

        var titles = ParseLines(draft.Text);
        if (titles.Length == 0) throw new ArgumentException("일정 제목을 하나 이상 입력하세요.");
        // Stage mutations on a separate list. If the filesystem rejects the
        // checkpoint, neither existing objects nor the collection are changed.
        // Publishing the snapshot only after SaveSnapshot succeeds also keeps
        // a multi-line edit atomic.
        var staged = new List<CalendarEvent>(Events);
        var existingIndex = existing is null ? -1 : Events.FindIndex(e => ReferenceEquals(e, existing));
        if (existing is not null && existingIndex < 0)
            throw new InvalidOperationException("수정하려는 일정이 현재 저장소에 없습니다.");
        var updated = 0;
        foreach (var title in titles)
        {
            CalendarEvent ev;
            if (updated == 0 && existing is not null)
            {
                ev = Copy(existing);
                staged[existingIndex] = ev;
            }
            else { ev = new CalendarEvent(); staged.Add(ev); }
            ev.Date = draft.StartDate.Date;
            ev.EndDate = draft.EndDate.Date;
            ev.StartAt = draft.StartAt;
            ev.EndAt = draft.EndAt;
            ev.CalendarId = draft.LocalOnly ? LocalCalendarId : draft.CalendarId;
            ev.LocalOnly = draft.LocalOnly;
            ev.Title = title;
            ev.Deleted = false;
            ev.Dirty = !draft.LocalOnly;
            ev.Conflict = false;
            ev.SyncError = null;
            ApplyRecurrenceDraft(ev, draft, updated == 0 ? existing : null);
            updated++;
        }
        SaveSnapshot(staged);
        Events = staged;
        return updated;
    }

    public void Delete(CalendarEvent ev)
    {
        if (IsReadOnly || ev.RemoteReadOnly) throw new InvalidOperationException("이 일정은 삭제할 수 없습니다.");
        var index = Events.FindIndex(item => ReferenceEquals(item, ev));
        if (index < 0) throw new InvalidOperationException("삭제하려는 일정이 현재 저장소에 없습니다.");
        var staged = new List<CalendarEvent>(Events);
        if (ev.RemoteId is null) staged.RemoveAt(index);
        else
        {
            var tombstone = Copy(ev);
            tombstone.Deleted = true;
            tombstone.Dirty = true;
            tombstone.Conflict = false;
            tombstone.SyncError = null;
            staged[index] = tombstone;
        }
        SaveSnapshot(staged);
        Events = staged;
    }

    private static CalendarEvent Copy(CalendarEvent source) => new()
    {
        Id = source.Id, Date = source.Date, EndDate = source.EndDate,
        StartAt = source.StartAt, EndAt = source.EndAt, Title = source.Title,
        CalendarId = source.CalendarId, RemoteId = source.RemoteId,
        RemoteCreatePending = source.RemoteCreatePending, RemoteETag = source.RemoteETag,
        Dirty = source.Dirty, Deleted = source.Deleted, Conflict = source.Conflict,
        RemoteReadOnly = source.RemoteReadOnly, LocalOnly = source.LocalOnly,
        SyncError = source.SyncError,
        Recurrence = source.Recurrence,
        RecurrenceExceptions = source.RecurrenceExceptions is null ? null : new List<DateTime>(source.RecurrenceExceptions),
        RecurringEventId = source.RecurringEventId,
        SeriesEditPending = source.SeriesEditPending, SeriesBaseStartAt = source.SeriesBaseStartAt,
        RemoteOrganizerOnly = source.RemoteOrganizerOnly
    };

    // Publish imported Google pages only after their durable checkpoint succeeds.
    // Copies are deliberately detached from the live collection: a failed
    // checkpoint must never leave partially imported events in memory.
    public List<CalendarEvent> CreateSnapshot() => Events.Select(Copy).ToList();

    public void CommitSnapshot(List<CalendarEvent> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SaveSnapshot(snapshot);
        Events = snapshot;
    }

    // A remote response is durable only when its acknowledgement and local
    // metadata are checkpointed together. Never mutate an Events entry before
    // SaveSnapshot succeeds; the function runs on a detached event clone.
    public bool CommitEventChange(Guid eventId, Func<CalendarEvent, bool> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var snapshot = CreateSnapshot();
        var candidate = snapshot.FirstOrDefault(e => e.Id == eventId);
        if (candidate is null) return false;
        if (!update(candidate)) return false;
        CommitSnapshot(snapshot);
        return true;
    }

    public bool CommitEventRemoval(Guid eventId)
    {
        var snapshot = CreateSnapshot();
        if (snapshot.RemoveAll(e => e.Id == eventId) == 0) return false;
        CommitSnapshot(snapshot);
        return true;
    }

    public void Save() => SaveSnapshot(Events);

    private void SaveSnapshot(IReadOnlyList<CalendarEvent> snapshot)
    {
        if (IsReadOnly) throw new IOException("Calendar store is in safe read-only mode.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temp, _path);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Calendar save failed", ex);
            throw;
        }
    }
}
