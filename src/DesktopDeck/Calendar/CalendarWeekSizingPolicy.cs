namespace DesktopDeck.Calendar;

/// <summary>
/// Minimum height for a week when a month occupies the current viewport.
/// Event lanes are allowed to grow beyond this minimum and invoke scrolling.
/// Pure math so the sizing decision can be tested without WPF/Windows.
/// </summary>
public static class CalendarWeekSizingPolicy
{
    public static double MinHeightPerWeek(double viewportHeight, double headingHeight,
        int weekCount, double minimumWeekHeight = 42, double rowGap = 3, double headingGap = 4)
    {
        if (weekCount <= 0 || !double.IsFinite(minimumWeekHeight) || minimumWeekHeight <= 0)
            return 42;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
            return minimumWeekHeight;
        if (!double.IsFinite(headingHeight) || headingHeight < 0) headingHeight = 0;
        var available = Math.Max(0, viewportHeight - headingHeight - headingGap - weekCount * rowGap);
        return Math.Max(minimumWeekHeight, available / weekCount);
    }
}
