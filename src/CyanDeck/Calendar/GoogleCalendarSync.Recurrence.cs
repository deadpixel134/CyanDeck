using System.Globalization;
using System.Net;
using System.Text.Json;
using CyanDeck.Services;

namespace CyanDeck.Calendar;

// Recurring events: upload of a local series, and "모든 반복 일정" changes of a
// Google series. Pulls keep singleEvents=true, so every instance still has its
// own id/ETag and "이 일정만" edits use the ordinary instance PATCH/DELETE.
public sealed partial class GoogleCalendarSync
{
    private bool NeedsRecurringRefresh(string calendarId)
        => _store.Events.Any(e => GoogleRecurringEditPolicy.NeedsRecurrenceRefresh(e)
            && string.Equals(ResolveCalendarId(e), calendarId, StringComparison.Ordinal));

    // Insert body for a local series, or null for an ordinary event. Google
    // expands a timed series in an IANA time zone, which is then required.
    private static object? RecurringInsertPayload(CalendarEvent sent)
    {
        if (sent.Recurrence is null || !CalendarRecurrenceRule.TryParse(sent.Recurrence, out _)) return null;
        var zone = TimeZoneInfo.Local;
        var timeZoneId = GoogleRecurrencePayload.ResolveIanaTimeZone(zone);
        object start, end;
        if (sent.StartAt is { } startAt && sent.EndAt is { } endAt)
        {
            start = new Dictionary<string, string> { ["dateTime"] = startAt.ToString("O"), ["timeZone"] = timeZoneId };
            end = new Dictionary<string, string> { ["dateTime"] = endAt.ToString("O"), ["timeZone"] = timeZoneId };
        }
        else
        {
            start = StartBody(sent);
            end = EndBody(sent);
        }
        return new Dictionary<string, object?>
        {
            ["id"] = sent.RemoteId,
            ["summary"] = sent.Title,
            ["start"] = start,
            ["end"] = end,
            ["recurrence"] = GoogleRecurrencePayload.Lines(sent, zone, timeZoneId),
            ["extendedProperties"] = new Dictionary<string, object>
            {
                ["private"] = new Dictionary<string, string> { ["desktopDeckLocalId"] = sent.Id.ToString("N") }
            }
        };
    }

    // PATCH body for an uploaded local series that Google has not yet replaced
    // with instances. The rule itself is never changed after upload.
    private static object? RecurringPatchPayload(CalendarEvent sent)
    {
        if (sent.Recurrence is null || sent.StartAt is not { } startAt || sent.EndAt is not { } endAt) return null;
        var timeZoneId = GoogleRecurrencePayload.ResolveIanaTimeZone(TimeZoneInfo.Local);
        return new Dictionary<string, object?>
        {
            ["summary"] = sent.Title,
            ["start"] = new Dictionary<string, string?> { ["date"] = null, ["dateTime"] = startAt.ToString("O"), ["timeZone"] = timeZoneId },
            ["end"] = new Dictionary<string, string?> { ["date"] = null, ["dateTime"] = endAt.ToString("O"), ["timeZone"] = timeZoneId }
        };
    }

    // A rejected series change becomes a conflict. A rejected series delete is
    // undone, so the hidden instances come back instead of vanishing for good.
    private void MarkSeriesConflict(Guid id)
    {
        _store.CommitEventChange(id, current =>
        {
            if (!CalendarRecurrenceExpander.UndoConflictedSeriesDelete(current)) current.Conflict = true;
            return true;
        });
    }

    private async Task<string> PushSeriesChangeAsync(CalendarEvent selected, string calendarId)
    {
        var id = selected.Id;
        if (!_preferences.Data.Catalog.Any(c => c.Id == calendarId && c.CanWrite))
        {
            MarkSeriesConflict(id);
            return "conflict";
        }
        var sent = _store.CreateSnapshot().First(x => x.Id == id);
        var seriesId = sent.RecurringEventId!;
        var seriesUrl = "calendars/" + Uri.EscapeDataString(calendarId) + "/events/" + Uri.EscapeDataString(seriesId);
        using var probe = await SendApiAsync(HttpMethod.Get, seriesUrl);
        if (probe.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            if (sent.Deleted) { RemoveSeriesLocally(calendarId, seriesId, id); return "ok"; }
            MarkSeriesConflict(id);
            return "conflict";
        }
        if (probe.StatusCode == HttpStatusCode.Forbidden)
        {
            MarkSeriesConflict(id);
            return "conflict";
        }
        if (!probe.IsSuccessStatusCode)
        {
            await ThrowIfEventRejectedAsync(probe);
            throw new HttpRequestException($"Google 반복 일정 조회 실패 {(int)probe.StatusCode}");
        }
        using var series = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
        var root = series.RootElement;
        var seriesTag = root.TryGetProperty("etag", out var tagProperty) ? tagProperty.GetString() : null;
        var cancelled = root.TryGetProperty("status", out var status) && status.GetString() == "cancelled";
        if (cancelled && sent.Deleted) { RemoveSeriesLocally(calendarId, seriesId, id); return "ok"; }
        if (cancelled || string.IsNullOrEmpty(seriesTag))
        {
            MarkSeriesConflict(id);
            return "conflict";
        }

        HttpResponseMessage response;
        if (sent.Deleted)
            response = await SendApiAsync(HttpMethod.Delete, seriesUrl, etag: seriesTag);
        else
        {
            var patch = new Dictionary<string, object?> { ["summary"] = sent.Title };
            if (sent.StartAt is { } instanceStart && sent.EndAt is { } instanceEnd
                && sent.SeriesBaseStartAt is { } baseStart
                && root.TryGetProperty("start", out var seriesStartPoint)
                && seriesStartPoint.TryGetProperty("dateTime", out var seriesStartValue)
                && DateTimeOffset.TryParse(seriesStartValue.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var seriesStart))
            {
                var (newStart, newEnd) = GoogleRecurringEditPolicy.ShiftSeriesTimes(seriesStart, baseStart, instanceStart, instanceEnd);
                var timeZone = seriesStartPoint.TryGetProperty("timeZone", out var zoneValue) ? zoneValue.GetString() : null;
                patch["start"] = SeriesTimeBody(newStart, timeZone);
                patch["end"] = SeriesTimeBody(newEnd, timeZone);
            }
            response = await SendApiAsync(HttpMethod.Patch, seriesUrl, patch, seriesTag);
        }

        using (response)
        {
            if (sent.Deleted && (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                || response.IsSuccessStatusCode))
            {
                RemoveSeriesLocally(calendarId, seriesId, id);
                return "ok";
            }
            // 412: the series changed on Google meanwhile. 403: not the organizer.
            if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict
                or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                MarkSeriesConflict(id);
                return "conflict";
            }
            if (!response.IsSuccessStatusCode)
            {
                // Like single events: a reply about this series only (e.g. 400 invalid) marks
                // this event instead of failing the whole sync on every run.
                await ThrowIfEventRejectedAsync(response);
                throw new HttpRequestException($"Google 반복 일정 변경 실패 {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
        }
        _store.CommitEventChange(id, current =>
        {
            current.SeriesEditPending = false;
            current.SeriesBaseStartAt = null;
            current.Conflict = false;
            // A newer local edit made while the request was outstanding stays pending.
            current.Dirty = !GoogleUploadReconciliation.SameOwnedFields(sent, current);
            return true;
        });
        // Every instance of the series changed: re-read this calendar in full
        // so instance titles/times (including exceptions) match Google.
        _preferences.UpdateSyncToken(calendarId, null);
        return "ok";
    }

    private static Dictionary<string, string> SeriesTimeBody(DateTimeOffset value, string? timeZone)
    {
        var body = new Dictionary<string, string> { ["dateTime"] = value.ToString("O") };
        if (!string.IsNullOrWhiteSpace(timeZone)) body["timeZone"] = timeZone;
        return body;
    }

    // After the series is gone on Google, drop its clean local instances.
    // Unsent local edits of other instances are kept and surface as conflicts.
    private void RemoveSeriesLocally(string calendarId, string seriesId, Guid tombstoneId)
    {
        var snapshot = _store.CreateSnapshot();
        snapshot.RemoveAll(e => e.Id == tombstoneId);
        var removed = snapshot.Where(e => !e.Dirty
            && string.Equals(e.RecurringEventId, seriesId, StringComparison.Ordinal)
            && string.Equals(ResolveCalendarId(e), calendarId, StringComparison.Ordinal)).ToList();
        foreach (var instance in removed) snapshot.Remove(instance);
        _store.CommitSnapshot(snapshot);
        DiagnosticsLog.Warn($"Google recurring series deleted; {removed.Count} local instances removed");
    }
}
