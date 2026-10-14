namespace CyanDeck.Calendar;

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
        // A lane is occupied only on the DAYS covered by its bars, not for the
        // entire week. Previously a multi-day all-day bar starting on Wednesday
        // pushed unrelated Tuesday timed events down because all-day events were
        // sorted first and laneEnds only remembered the bar's last day.
        //
        // Seven columns fit in a 7-bit mask. Reusing a lane on disjoint days
        // also keeps each multi-day bar on one continuous horizontal row.
        var occupiedColumnsByLane = new List<int>();
        var allDayBottomByColumn = new int[7];
        var result = new List<WeekBar>(spans.Length);
        foreach (var span in spans)
        {
            var start = (int)(span.Start - weekStart.Date).TotalDays;
            var end = (int)(span.End - weekStart.Date).TotalDays;
            var columnMask = ((1 << (end - start + 1)) - 1) << start;

            // Timed bars may reuse lanes occupied by all-day bars on OTHER
            // dates, but must sit below the lowest all-day lane touched by
            // their own span. This includes two- or three-day timed events.
            var lane = 0;
            if (!span.Event.IsAllDay)
                for (var column = start; column <= end; column++)
                    lane = Math.Max(lane, allDayBottomByColumn[column]);

            while (lane < occupiedColumnsByLane.Count &&
                   (occupiedColumnsByLane[lane] & columnMask) != 0)
                lane++;

            if (lane == occupiedColumnsByLane.Count)
                occupiedColumnsByLane.Add(columnMask);
            else
                occupiedColumnsByLane[lane] |= columnMask;

            if (span.Event.IsAllDay)
                for (var column = start; column <= end; column++)
                    allDayBottomByColumn[column] = Math.Max(allDayBottomByColumn[column], lane + 1);

            result.Add(new WeekBar(span.Event, start, end - start + 1, lane));
        }
        return result;
    }
}
