namespace DesktopDeck.Calendar;

/// <summary>Pure policy: catalog metadata changes rarely; events still use syncToken each poll.</summary>
public static class GoogleCalendarCatalogRefreshPolicy
{
    public static readonly TimeSpan RefreshPeriod = TimeSpan.FromHours(6);

    public static bool ShouldRefresh(CalendarPreferences preferences, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var updated = preferences.CalendarCatalogRefreshedUtc;
        return preferences.Catalog is null || preferences.Catalog.Count == 0
            || updated == DateTime.MinValue || updated.Kind != DateTimeKind.Utc
            || utcNow < updated || utcNow - updated >= RefreshPeriod;
    }
}
