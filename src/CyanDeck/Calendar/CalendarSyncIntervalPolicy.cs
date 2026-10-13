namespace CyanDeck.Calendar;

/// <summary>Pure validation for the calendar's per-user Google polling interval.</summary>
public static class CalendarSyncIntervalPolicy
{
    public const int DefaultMinutes = 15;
    public const int MinMinutes = 0; // 0 disables automatic polling; manual sync remains available.
    public const int MaxMinutes = 1440;

    public static bool IsValid(int minutes) => minutes is >= MinMinutes and <= MaxMinutes;
    public static int Normalize(int minutes) => IsValid(minutes) ? minutes : DefaultMinutes;
    public static TimeSpan ToInterval(int minutes) => TimeSpan.FromMinutes(Normalize(minutes));
}
