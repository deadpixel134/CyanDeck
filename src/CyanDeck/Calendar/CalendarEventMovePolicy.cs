namespace CyanDeck.Calendar;

// Date relocation is computed without mutating the persisted event. The store
// validates and checkpoints the result before updating live state.
public static class CalendarEventMovePolicy
{
    public static CalendarEventDraft CreateDraft(CalendarEvent source, DateTime targetDate,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var dayDelta = (targetDate.Date - source.Date.Date).Days;
        var zone = timeZone ?? TimeZoneInfo.Local;
        var start = source.StartAt is { } startAt ? MoveTime(startAt, dayDelta, zone) : (DateTimeOffset?)null;
        var end = source.EndAt is { } endAt ? MoveTime(endAt, dayDelta, zone) : (DateTimeOffset?)null;
        return new CalendarEventDraft
        {
            StartDate = source.Date.Date.AddDays(dayDelta),
            EndDate = source.IsAllDay ? source.EffectiveEndDate.AddDays(dayDelta)
                : end?.Date ?? source.EffectiveEndDate.AddDays(dayDelta),
            StartAt = start, EndAt = end,
            Text = source.Title, CalendarId = source.CalendarId ?? "primary",
            LocalOnly = source.LocalOnly
        };
    }

    private static DateTimeOffset MoveTime(DateTimeOffset value, int dayDelta, TimeZoneInfo zone)
    {
        var destinationLocal = TimeZoneInfo.ConvertTime(value, zone).DateTime.AddDays(dayDelta);
        if (zone.IsInvalidTime(destinationLocal))
            throw new ArgumentException("이동 대상의 현지 시각이 존재하지 않습니다. 편집에서 시간을 조정하세요.");
        return new DateTimeOffset(destinationLocal, zone.GetUtcOffset(destinationLocal));
    }
}
