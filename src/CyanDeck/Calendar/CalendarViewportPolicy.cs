namespace CyanDeck.Calendar;

/// <summary>Pure, testable week viewport calculation; Sunday is week start.</summary>
public static class CalendarViewportPolicy
{
    public static (DateTime FirstSunday, int WeekCount) Calculate(
        DateTime anchor, string? mode, int configuredRows)
    {
        int rows = Math.Clamp(configuredRows, 3, 8);
        if (mode == "Month")
        {
            var firstOfMonth = new DateTime(anchor.Year, anchor.Month, 1);
            var firstSunday = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
            int required = ((int)firstOfMonth.DayOfWeek
                + DateTime.DaysInMonth(anchor.Year, anchor.Month) + 6) / 7;
            // Never truncate a month just because the requested minimum is smaller.
            return (firstSunday, Math.Max(rows, required));
        }
        var anchorSunday = anchor.Date.AddDays(-(int)anchor.DayOfWeek);
        return (anchorSunday.AddDays(-7 * ((rows - 1) / 2)), rows);
    }

    public static DateTime Navigate(DateTime anchor, string? mode, int rows, int direction)
        => mode == "Month" ? anchor.AddMonths(direction)
            : anchor.AddDays(direction * 7 * Math.Clamp(rows, 3, 8));
}
