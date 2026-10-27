using System.Globalization;

namespace CyanDeck.Calendar;

// Pure start/end linking rules for the event editor (unit-tested).
// Values are local wall-clock DateTime; the editor converts to offsets on save.
public static class CalendarEditorTimePolicy
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    // Changing the start keeps the event length; an invalid previous length
    // (end not after start) falls back to one hour.
    public static DateTime ShiftEnd(DateTime previousStart, DateTime previousEnd, DateTime newStart)
    {
        var duration = previousEnd - previousStart;
        if (duration <= TimeSpan.Zero) duration = DefaultDuration;
        return newStart + duration;
    }

    // An end time earlier than the start on the same date means the next day
    // (e.g. 23:00 ~ 01:00), so a late-night event never fails at save.
    public static DateTime ResolveTimedEnd(DateTime startDate, TimeSpan startTime, DateTime endDate, TimeSpan endTime)
    {
        var start = startDate.Date + startTime;
        var end = endDate.Date + endTime;
        if (end < start && endDate.Date == startDate.Date) end = end.AddDays(1);
        return end;
    }

    // Hint under the time pickers, or null when nothing needs explaining.
    public static string? EndHint(DateTime startDate, TimeSpan startTime, DateTime endDate, TimeSpan endTime)
    {
        var start = startDate.Date + startTime;
        var end = ResolveTimedEnd(startDate, startTime, endDate, endTime);
        if (end == start) return "종료 시간이 시작 시간과 같습니다. 종료 시간을 바꾸세요.";
        if (end < start) return "종료가 시작보다 빠릅니다. 종료 날짜나 시간을 바꾸세요.";
        if (endDate.Date == startDate.Date && end.Date > start.Date)
            return "다음 날 " + end.ToString("HH:mm", CultureInfo.InvariantCulture) + "에 끝남";
        return null;
    }

    // Keyboard entry: "1430", "14:30", "14.30", "930", "9:05", "9".
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim().Replace(" ", "");
        int hour, minute;
        var separator = value.IndexOfAny(new[] { ':', '.', 'h', 'H' });
        if (separator >= 0)
        {
            var hourText = value[..separator];
            var minuteText = value[(separator + 1)..];
            if (minuteText.Length == 0) minuteText = "0";
            if (hourText.Length is < 1 or > 2 || minuteText.Length > 2
                || !int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out hour)
                || !int.TryParse(minuteText, NumberStyles.None, CultureInfo.InvariantCulture, out minute))
                return false;
        }
        else
        {
            if (value.Length is < 1 or > 4 || !value.All(char.IsAsciiDigit)) return false;
            if (value.Length <= 2) { hour = int.Parse(value, CultureInfo.InvariantCulture); minute = 0; }
            else
            {
                hour = int.Parse(value[..^2], CultureInfo.InvariantCulture);
                minute = int.Parse(value[^2..], CultureInfo.InvariantCulture);
            }
        }
        if (hour is < 0 or > 23 || minute is < 0 or > 59) return false;
        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    // Minute list in 5-minute steps; an existing odd minute (e.g. 07) is kept.
    public static IReadOnlyList<int> MinuteChoices(int keepMinute)
    {
        var minutes = Enumerable.Range(0, 12).Select(i => i * 5).ToList();
        if (keepMinute is >= 0 and <= 59 && !minutes.Contains(keepMinute)) minutes.Add(keepMinute);
        minutes.Sort();
        return minutes;
    }
}
