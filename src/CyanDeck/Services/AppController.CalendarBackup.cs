using CyanDeck.Calendar;

namespace CyanDeck.Services;

public sealed partial class AppController
{
    // A layout/settings export holds no calendar events, and local-only events have no
    // other copy. Write them next to the exported file in the calendar backup format
    // (importable from the calendar settings). Nothing is written when there are none,
    // and a failure here never fails the layout export itself.
    private void ExportCalendarCompanion(string configurationPath)
    {
        try
        {
            var written = _calendar is not null
                ? _calendar.ExportLocalEventsCompanion(configurationPath)
                : CalendarEventBackup.ExportCompanion(new CalendarEventStore().Events, configurationPath);
            if (written > 0)
                DiagnosticsLog.Info($"Exported {written} local calendar events next to the configuration backup");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("Local calendar events were not exported with the configuration backup: " + ex.Message);
        }
    }
}
