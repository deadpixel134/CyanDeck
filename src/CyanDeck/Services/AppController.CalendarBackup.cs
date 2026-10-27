using CyanDeck.Calendar;

namespace CyanDeck.Services;

public sealed partial class AppController
{
    // A layout/settings export holds no calendar events, and local-only events have no
    // other copy. Write them next to the exported file in the calendar backup format
    // (importable from the calendar settings). Nothing is written when there are none,
    // and a failure here never fails the layout export itself.
    // Returns the number of local events written, or -1 when the companion could not be written.
    private int ExportCalendarCompanion(string configurationPath)
    {
        try
        {
            int written;
            if (_calendar is not null)
                written = _calendar.ExportLocalEventsCompanion(configurationPath);
            else
            {
                // A store that could not read its files must not empty an existing companion.
                var events = new CalendarEventStore();
                written = CalendarEventBackup.ExportCompanion(events.Events, configurationPath,
                    eventsUnavailable: events.IsReadOnly && !events.IsRecoveredFromBackup);
            }
            if (written > 0)
                DiagnosticsLog.Info($"Exported {written} local calendar events next to the configuration backup");
            return written;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("Local calendar events were not exported with the configuration backup: " + ex.Message);
            return -1;
        }
    }
}
