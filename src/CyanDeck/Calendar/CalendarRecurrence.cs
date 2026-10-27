using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CyanDeck.Calendar;

public enum CalendarRecurrenceFrequency { Daily, Weekly, Monthly, Yearly }

// RFC 5545 RRULE subset used by CyanDeck-created series:
// FREQ=DAILY|WEEKLY|MONTHLY|YEARLY, INTERVAL, BYDAY (weekly only, no ordinals),
// and UNTIL (inclusive local date) or COUNT. Anything else is rejected so a
// rule is never expanded differently from how Google would expand it.
public sealed class CalendarRecurrenceRule
{
    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
    private const string KoreanDayNames = "일월화수목금토";

    public CalendarRecurrenceFrequency Frequency { get; init; }
    public int Interval { get; init; } = 1;
    public IReadOnlyList<DayOfWeek> ByDays { get; init; } = Array.Empty<DayOfWeek>();
    public DateTime? Until { get; init; }
    public int? Count { get; init; }

    public const int MaxInterval = 99;
    public const int MaxCount = 999;

    public static bool TryParse(string? text, [NotNullWhen(true)] out CalendarRecurrenceRule? rule)
    {
        rule = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var body = text.Trim();
        if (body.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) body = body[6..];
        CalendarRecurrenceFrequency? frequency = null;
        var interval = 1;
        var days = new List<DayOfWeek>();
        DateTime? until = null;
        int? count = null;
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) return false;
            var key = part[..separator].ToUpperInvariant();
            var value = part[(separator + 1)..].Trim().ToUpperInvariant();
            switch (key)
            {
                case "FREQ":
                    frequency = value switch
                    {
                        "DAILY" => CalendarRecurrenceFrequency.Daily,
                        "WEEKLY" => CalendarRecurrenceFrequency.Weekly,
                        "MONTHLY" => CalendarRecurrenceFrequency.Monthly,
                        "YEARLY" => CalendarRecurrenceFrequency.Yearly,
                        _ => null
                    };
                    if (frequency is null) return false;
                    break;
                case "INTERVAL":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out interval)
                        || interval < 1 || interval > MaxInterval) return false;
                    break;
                case "BYDAY":
                    foreach (var code in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var index = Array.IndexOf(DayCodes, code);
                        if (index < 0) return false;
                        if (!days.Contains((DayOfWeek)index)) days.Add((DayOfWeek)index);
                    }
                    break;
                case "UNTIL":
                    if (!TryParseUntil(value, out var untilDate)) return false;
                    until = untilDate;
                    break;
                case "COUNT":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount)
                        || parsedCount < 1 || parsedCount > MaxCount) return false;
                    count = parsedCount;
                    break;
                case "WKST":
                    if (value != "MO") return false;
                    break;
                default:
                    return false;
            }
        }
        if (frequency is null || (until.HasValue && count.HasValue)) return false;
        if (days.Count > 0 && frequency != CalendarRecurrenceFrequency.Weekly) return false;
        rule = new CalendarRecurrenceRule
        {
            Frequency = frequency.Value, Interval = interval,
            ByDays = days.OrderBy(MondayOffset).ToArray(), Until = until, Count = count
        };
        return true;
    }

    private static bool TryParseUntil(string value, out DateTime date)
    {
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (value.EndsWith('Z') && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            date = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().Date;
            return true;
        }
        if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var floating))
        {
            date = floating.Date;
            return true;
        }
        date = default;
        return false;
    }

    internal static int MondayOffset(DayOfWeek day) => ((int)day + 6) % 7;

    // Canonical local form, e.g. "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20261231".
    public override string ToString() => Format(Until?.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

    // Google expands a timed series in its time zone; RFC 5545 then requires a
    // UTC UNTIL. Use the end of the chosen local day so that day is included.
    public string ToGoogleRule(bool timed, TimeZoneInfo zone)
    {
        if (Until is not { } until) return Format(null);
        if (!timed) return Format(until.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        var lastLocal = DateTime.SpecifyKind(until.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(zone.IsInvalidTime(lastLocal) ? lastLocal.AddHours(1) : lastLocal, zone);
        return Format(utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
    }

    private string Format(string? until)
    {
        var builder = new StringBuilder("FREQ=").Append(Frequency.ToString().ToUpperInvariant());
        if (Interval > 1) builder.Append(";INTERVAL=").Append(Interval.ToString(CultureInfo.InvariantCulture));
        if (Frequency == CalendarRecurrenceFrequency.Weekly && ByDays.Count > 0)
            builder.Append(";BYDAY=").Append(string.Join(",", ByDays.OrderBy(MondayOffset).Select(d => DayCodes[(int)d])));
        if (until is not null) builder.Append(";UNTIL=").Append(until);
        else if (Count is { } count) builder.Append(";COUNT=").Append(count.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    // Short Korean summary for the editor and tooltips, e.g. "2주마다 (월, 수) · 10회".
    public string Describe()
    {
        var unit = Frequency switch
        {
            CalendarRecurrenceFrequency.Daily => "일",
            CalendarRecurrenceFrequency.Weekly => "주",
            CalendarRecurrenceFrequency.Monthly => "개월",
            _ => "년"
        };
        var simple = Frequency switch
        {
            CalendarRecurrenceFrequency.Daily => "매일",
            CalendarRecurrenceFrequency.Weekly => "매주",
            CalendarRecurrenceFrequency.Monthly => "매월",
            _ => "매년"
        };
        var text = Interval > 1 ? Interval.ToString(CultureInfo.InvariantCulture) + unit + "마다" : simple;
        if (Frequency == CalendarRecurrenceFrequency.Weekly && ByDays.Count > 0)
            text += " (" + string.Join(", ", ByDays.OrderBy(MondayOffset).Select(d => KoreanDayNames[(int)d].ToString())) + ")";
        if (Until is { } until) text += " · " + until.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + "까지";
        else if (Count is { } count) text += " · " + count.ToString(CultureInfo.InvariantCulture) + "회";
        return text;
    }

    // Candidate occurrence dates in order, starting with the series start date.
    // Months/years without the start day (31st, Feb 29) are skipped like RFC 5545.
    internal IEnumerable<DateTime> CandidateDates(DateTime start)
    {
        start = start.Date;
        switch (Frequency)
        {
            case CalendarRecurrenceFrequency.Daily:
                for (var k = 0; ; k++) yield return start.AddDays((double)k * Interval);
            case CalendarRecurrenceFrequency.Weekly:
            {
                var days = ByDays.Count > 0 ? ByDays.OrderBy(MondayOffset).ToArray() : new[] { start.DayOfWeek };
                var weekStart = start.AddDays(-MondayOffset(start.DayOfWeek));
                for (var week = 0; ; week += Interval)
                    foreach (var day in days)
                    {
                        var date = weekStart.AddDays(week * 7 + MondayOffset(day));
                        if (date >= start) yield return date;
                    }
            }
            case CalendarRecurrenceFrequency.Monthly:
                for (var k = 0; ; k++)
                {
                    var month = new DateTime(start.Year, start.Month, 1).AddMonths(k * Interval);
                    if (start.Day <= DateTime.DaysInMonth(month.Year, month.Month))
                        yield return new DateTime(month.Year, month.Month, start.Day);
                }
            default:
                for (var k = 0; ; k++)
                {
                    var year = start.Year + k * Interval;
                    if (year > 9998) yield break;
                    if (start.Day <= DateTime.DaysInMonth(year, start.Month))
                        yield return new DateTime(year, start.Month, start.Day);
                }
        }
    }
}

public sealed record CalendarRecurrenceOccurrence(CalendarEvent Event, CalendarEvent Master, DateTime OccurrenceDate);

// Pure display expansion of local series. Occurrences are detached clones that
// keep the master's Id (shared colour), so they never enter the store.
public static class CalendarRecurrenceExpander
{
    private const int MaxCandidates = 20000;

    public static IReadOnlyList<DateTime> OccurrenceDates(CalendarEvent master, CalendarRecurrenceRule rule,
        DateTime rangeStart, DateTime rangeEndExclusive)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(rule);
        var result = new List<DateTime>();
        var start = master.Date.Date;
        var spanDays = Math.Max(0, (master.EffectiveEndDate - start).Days);
        var windowStart = rangeStart.Date.AddDays(-spanDays);
        var excluded = new HashSet<DateTime>((master.RecurrenceExceptions ?? new List<DateTime>()).Select(d => d.Date));
        var produced = 0;
        var examined = 0;
        foreach (var date in rule.CandidateDates(start))
        {
            if (++examined > MaxCandidates) break;
            if (rule.Until is { } until && date > until.Date) break;
            if (rule.Count is { } count && produced >= count) break;
            if (date >= rangeEndExclusive.Date) break;
            produced++; // COUNT includes excluded dates (EXDATE applies after RRULE).
            if (date < windowStart || excluded.Contains(date)) continue;
            result.Add(date);
        }
        return result;
    }

    public static IReadOnlyList<CalendarRecurrenceOccurrence> Expand(CalendarEvent master,
        DateTime rangeStart, DateTime rangeEndExclusive, TimeZoneInfo? timeZone = null)
    {
        if (!CalendarRecurrenceRule.TryParse(master.Recurrence, out var rule)) return [];
        var zone = timeZone ?? TimeZoneInfo.Local;
        var spanDays = Math.Max(0, (master.EffectiveEndDate - master.Date.Date).Days);
        var list = new List<CalendarRecurrenceOccurrence>();
        foreach (var date in OccurrenceDates(master, rule, rangeStart, rangeEndExclusive))
        {
            var occurrence = new CalendarEvent
            {
                Id = master.Id, Date = date, EndDate = date.AddDays(spanDays), Title = master.Title,
                CalendarId = master.CalendarId, RemoteId = master.RemoteId,
                RemoteCreatePending = master.RemoteCreatePending, RemoteETag = master.RemoteETag,
                Dirty = master.Dirty, Deleted = false, Conflict = master.Conflict,
                RemoteReadOnly = master.RemoteReadOnly, LocalOnly = master.LocalOnly,
                Recurrence = master.Recurrence, RecurrenceExceptions = master.RecurrenceExceptions
            };
            if (master.StartAt is { } startAt && master.EndAt is { } endAt)
            {
                var wall = DateTime.SpecifyKind(date.Date + TimeZoneInfo.ConvertTime(startAt, zone).TimeOfDay,
                    DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
                var occurrenceStart = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
                occurrence.StartAt = occurrenceStart;
                occurrence.EndAt = occurrenceStart + (endAt - startAt);
            }
            list.Add(new CalendarRecurrenceOccurrence(occurrence, master, date));
        }
        return list;
    }

    // After the first sync Google returns the series as instances whose
    // recurringEventId is the master's RemoteId; never show both.
    public static bool IsReplacedByGoogleInstances(CalendarEvent master, IEnumerable<CalendarEvent> all)
        => master.Recurrence is not null && !string.IsNullOrEmpty(master.RemoteId)
            && all.Any(e => !ReferenceEquals(e, master) && !e.Deleted
                && string.Equals(e.RecurringEventId, master.RemoteId, StringComparison.Ordinal));

    // Removes uploaded local masters that Google instances now represent.
    public static int RemoveReplacedMasters(List<CalendarEvent> snapshot)
    {
        var replaced = snapshot.Where(e => !e.Dirty && !e.RemoteCreatePending && !e.Conflict
            && IsReplacedByGoogleInstances(e, snapshot)).ToList();
        foreach (var master in replaced) snapshot.Remove(master);
        return replaced.Count;
    }

    // Instances of a series whose delete is waiting for upload stay hidden.
    // A delete that ended in a conflict no longer hides anything.
    public static bool IsHiddenBySeriesDelete(CalendarEvent ev, IEnumerable<CalendarEvent> all)
        => ev.IsRecurringInstance && all.Any(e => e.Deleted && e.SeriesEditPending && !e.Conflict
            && string.Equals(e.RecurringEventId, ev.RecurringEventId, StringComparison.Ordinal));

    // A series CyanDeck uploaded has the Google id "dd" + local master id, so
    // its Google instances can still find the colour set on the local series.
    public static Guid? SeriesLocalId(string? recurringEventId)
        => recurringEventId is { Length: 34 } id && id.StartsWith("dd", StringComparison.Ordinal)
            && Guid.TryParseExact(id.Substring(2), "N", out var localId) ? (Guid?)localId : null;

    // "모든 반복 일정" edited from one occurrence of a local series: the series
    // start moves by the occurrence's date shift. A weekly series that repeats on
    // several weekdays cannot be moved this way (the day set would be ambiguous);
    // when the first weekday was unchecked the series starts on the next chosen day.
    public static DateTime ResolveSeriesStart(DateTime masterStart, int shiftDays, CalendarRecurrenceRule? rule)
    {
        var start = masterStart.Date.AddDays(shiftDays);
        if (rule is null || rule.Frequency != CalendarRecurrenceFrequency.Weekly || rule.ByDays.Count == 0) return start;
        if (shiftDays != 0 && rule.ByDays.Count > 1)
            throw new InvalidOperationException("여러 요일에 반복하는 일정은 '모든 반복 일정'으로 날짜를 옮길 수 없습니다. 요일을 바꾸거나 '이 일정만'을 선택하세요.");
        for (var i = 0; i < 7 && !rule.ByDays.Contains(start.DayOfWeek); i++) start = start.AddDays(1);
        return start;
    }

    // A rejected "모든 반복 일정" delete is undone: the instance is shown again
    // with ⚠ so the conflict menu (Google 원격 버전으로 복원) can resolve it.
    public static bool UndoConflictedSeriesDelete(CalendarEvent ev)
    {
        if (!ev.Deleted || !ev.SeriesEditPending || !ev.IsRecurringInstance) return false;
        ev.Deleted = false;
        ev.SeriesEditPending = false;
        ev.SeriesBaseStartAt = null;
        ev.Dirty = false;
        ev.Conflict = true;
        return true;
    }

    // Pull marks a dirty tombstone Conflict when Google changed meanwhile.
    public static int RevealConflictedSeriesDeletes(List<CalendarEvent> snapshot)
        => snapshot.Count(e => e.Conflict && UndoConflictedSeriesDelete(e));
}

// Google payload helpers for uploading a local series (pure, unit-tested).
public static class GoogleRecurrencePayload
{
    // Google requires an IANA time zone for a timed recurring event.
    public static string ResolveIanaTimeZone(TimeZoneInfo zone)
    {
        if (zone.HasIanaId) return zone.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && !string.IsNullOrWhiteSpace(iana))
            return iana;
        var offset = zone.BaseUtcOffset;
        if (offset == TimeSpan.Zero) return "UTC";
        if (offset.Minutes != 0) return "UTC";
        // POSIX-style Etc zones use the inverted sign.
        return "Etc/GMT" + (offset > TimeSpan.Zero ? "-" : "+") + Math.Abs(offset.Hours).ToString(CultureInfo.InvariantCulture);
    }

    public static string[] Lines(CalendarEvent master, TimeZoneInfo zone, string timeZoneId)
    {
        if (!CalendarRecurrenceRule.TryParse(master.Recurrence, out var rule)) return [];
        var timed = master.StartAt.HasValue;
        var lines = new List<string> { "RRULE:" + rule.ToGoogleRule(timed, zone) };
        var exceptions = (master.RecurrenceExceptions ?? new List<DateTime>()).Select(d => d.Date).Distinct().OrderBy(d => d).ToArray();
        if (exceptions.Length > 0)
        {
            if (!timed)
                lines.Add("EXDATE;VALUE=DATE:" + string.Join(",", exceptions.Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture))));
            else
            {
                var time = TimeZoneInfo.ConvertTime(master.StartAt!.Value, zone).TimeOfDay;
                lines.Add("EXDATE;TZID=" + timeZoneId + ":" + string.Join(",",
                    exceptions.Select(d => (d + time).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture))));
            }
        }
        return lines.ToArray();
    }
}

// Which pulled Google instances may be edited ("이 일정만" / "모든 반복 일정").
// Only the organizer's copy (organizer.self) or guestsCanModify allows writes;
// anything else would be rejected by Google with forbiddenForNonOrganizer.
public static class GoogleRecurringEditPolicy
{
    public static string? RecurringEventId(JsonElement item)
        => item.TryGetProperty("recurringEventId", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static bool IsReadOnlyRecurring(JsonElement item)
    {
        if (item.TryGetProperty("recurrence", out _)) return true; // series master listings are not edited here
        if (string.IsNullOrEmpty(RecurringEventId(item))) return false;
        if (item.TryGetProperty("guestsCanModify", out var guests) && guests.ValueKind == JsonValueKind.True) return false;
        if (!item.TryGetProperty("organizer", out var organizer) || organizer.ValueKind != JsonValueKind.Object) return false;
        return !(organizer.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True);
    }

    // True when the organizer rule is the only reason a pulled instance is
    // read-only (writable calendar, ordinary event type, not locked).
    public static bool IsOrganizerOnly(JsonElement item, bool calendarWritable)
    {
        if (!calendarWritable || string.IsNullOrEmpty(RecurringEventId(item)) || !IsReadOnlyRecurring(item)) return false;
        if (item.TryGetProperty("eventType", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() is not null and not "default") return false;
        return !(item.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.True);
    }

    // Stored value: series id, "" for an instance-style id that is not
    // recurring (so the one-time refresh below never repeats), else null.
    public static string? StoredRecurringEventId(JsonElement item, string remoteId)
        => RecurringEventId(item) is { Length: > 0 } id ? id : remoteId.Contains('_') ? "" : null;

    // Instances imported before this version were all read-only and carry no
    // series id; one full listing of their calendar refreshes them.
    public static bool NeedsRecurrenceRefresh(CalendarEvent ev)
        => !ev.Deleted && ev.RemoteReadOnly && ev.RecurringEventId is null
            && ev.RemoteId is not null && ev.RemoteId.Contains('_');

    // "모든 반복 일정" time change: shift the series start by the same amount
    // the instance start moved, and keep the edited duration.
    public static (DateTimeOffset Start, DateTimeOffset End) ShiftSeriesTimes(DateTimeOffset seriesStart,
        DateTimeOffset instanceStartBefore, DateTimeOffset instanceStart, DateTimeOffset instanceEnd)
    {
        var start = seriesStart + (instanceStart - instanceStartBefore);
        return (start, start + (instanceEnd - instanceStart));
    }
}
