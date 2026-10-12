namespace DesktopDeck.Calendar;

/// <summary>
/// Pure, locale-independent calendar title formatting. The title always reports
/// today's actual date; navigating the viewport does not silently relabel today.
/// </summary>
public static class CalendarHeaderPolicy
{
    private static readonly string[] KoreanWeekdays = ["일", "월", "화", "수", "목", "금", "토"];

    public static string FormatToday(DateTime today)
        => $"{today:yyyy.MM.dd} ({KoreanWeekdays[(int)today.DayOfWeek]})";

    // Dates are the actually rendered Sunday-to-Saturday viewport, not the month anchor.
    public static string FormatVisibleRangeAndToday(DateTime firstSunday, DateTime lastSaturday, string todayCaption)
        => $"{firstSunday:yyyy.MM.dd} ~ {lastSaturday:yyyy.MM.dd} - 오늘: {todayCaption}";

    public static string FormatViewportHint(DateTime anchor, string? mode,
        DateTime firstSunday, DateTime lastSaturday)
    {
        var period = mode == "Month" ? $"{anchor:yyyy년 M월} 보기" : "오늘 중심 주 단위 보기";
        return $"{period} · {firstSunday:yyyy.MM.dd} ~ {lastSaturday:yyyy.MM.dd} · 드래그로 이동";
    }
}
