namespace DesktopDeck.Calendar;

// Pure layout engine. Intervals use INCLUSIVE local display dates, split at each week.
public sealed record WeekBar(CalendarEvent Event, int StartColumn, int ColumnSpan, int Lane);

public static class WeekBarLayout
{
    public static IReadOnlyList<WeekBar> Build(DateTime weekStart, IEnumerable<CalendarEvent> events)
    {
        var weekEnd = weekStart.Date.AddDays(6);
        var spans = events.Where(e => !e.Deleted)
            .Select(e => new
            {
                Event = e,
                Start = e.Date.Date > weekStart.Date ? e.Date.Date : weekStart.Date,
                End = e.EffectiveEndDate < weekEnd ? e.EffectiveEndDate : weekEnd
            })
            .Where(e => e.Start <= e.End)
            .OrderBy(e => e.Event.IsAllDay ? 0 : 1) // Reserve top lanes for all-day spans.
            .ThenBy(e => e.Start)
            .ThenBy(e => e.Event.StartAt?.ToLocalTime().TimeOfDay ?? TimeSpan.Zero)
            .ThenByDescending(e => e.End) // Stable multi-day bars at equal start time.
            .ThenBy(e => e.Event.Id)
            .ToArray();
        var laneEnds = new List<int>();
        var result = new List<WeekBar>(spans.Length);
        foreach (var span in spans)
        {
            var start = (int)(span.Start - weekStart.Date).TotalDays;
            var end = (int)(span.End - weekStart.Date).TotalDays;
            var lane = laneEnds.FindIndex(previousEnd => previousEnd < start);
            if (lane == -1) { lane = laneEnds.Count; laneEnds.Add(end); }
            else laneEnds[lane] = end;
            result.Add(new WeekBar(span.Event, start, end - start + 1, lane));
        }
        return result;
    }
}
