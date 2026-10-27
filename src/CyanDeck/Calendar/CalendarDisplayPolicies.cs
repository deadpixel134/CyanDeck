namespace CyanDeck.Calendar;

/// <summary>
/// Pure rules for keeping "today" current while the calendar stays open across midnight,
/// a clock/time-zone change or a resume from sleep. Sunday is week start, as elsewhere.
/// </summary>
public static class CalendarTodayRefreshPolicy
{
    // Re-check at least hourly so a missed clock event, DST or a time-zone change still
    // lands within the hour. The next-midnight delay is recomputed on every arm.
    public static readonly TimeSpan MaximumCheckInterval = TimeSpan.FromHours(1);
    // While editing, dragging, a menu or the settings panel is open, retry this often.
    public static readonly TimeSpan BusyRetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MidnightMargin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(1);

    public static TimeSpan DelayUntilNextCheck(DateTime now)
    {
        var delay = now.Date.AddDays(1) - now + MidnightMargin;
        if (delay < MinimumDelay) return MinimumDelay;
        return delay > MaximumCheckInterval ? MaximumCheckInterval : delay;
    }

    /// <summary>
    /// After the local date changed, follow the new today only when the user was looking at
    /// the old today: the month containing it in month view, or a rolling viewport that still
    /// showed it. A user who paged to another period keeps that period.
    /// </summary>
    public static bool ShouldFollowToday(DateTime anchor, string? mode, int configuredRows, DateTime previousToday)
    {
        var previous = previousToday.Date;
        if (mode == "Month")
            return anchor.Year == previous.Year && anchor.Month == previous.Month;
        var viewport = CalendarViewportPolicy.Calculate(anchor, mode, configuredRows);
        var lastDate = viewport.FirstSunday.AddDays(viewport.WeekCount * 7 - 1);
        return previous >= viewport.FirstSunday && previous <= lastDate;
    }
}

/// <summary>
/// The calendar window itself stays fully opaque so dates, events and bars are always crisp.
/// Only the background brushes follow the idle / in-use (chameleon) opacity sliders, and the
/// slider value is the final background opacity across its whole 15%..100% range.
/// </summary>
public static class CalendarOpacityPolicy
{
    public const double WindowOpacity = 1.0;
    public const double MinimumBackgroundOpacity = 0.15;
    public const double DefaultIdleBackgroundOpacity = 0.38;
    public const double DefaultActiveBackgroundOpacity = 0.86;
    // Matches the previous effective floor of today's cell (0.7 x the old 0.86 window).
    public const double MinimumTodayBackgroundOpacity = 0.6;

    public static double BackgroundOpacity(bool fullPresentation, double activeBackground, double idleBackground)
        => fullPresentation
            ? Normalize(activeBackground, DefaultActiveBackgroundOpacity)
            : Normalize(idleBackground, DefaultIdleBackgroundOpacity);

    public static double TodayBackgroundOpacity(double backgroundOpacity)
        => Math.Max(backgroundOpacity, MinimumTodayBackgroundOpacity);

    private static double Normalize(double value, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, MinimumBackgroundOpacity, 1.0) : fallback;
}
