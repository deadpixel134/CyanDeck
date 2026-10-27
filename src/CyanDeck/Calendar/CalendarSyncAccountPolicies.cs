using System.Globalization;

namespace CyanDeck.Calendar;

public enum CalendarPromptSyncDecision { Run, RetryLater, Skip }

// A local save, move or delete of a Google-linked event is uploaded a few seconds
// later instead of waiting for the polling interval. Never opens a browser login:
// the caller passes canSyncSilently=false when no usable saved token exists.
public static class CalendarPromptSyncPolicy
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(10);

    // Same filter as the GoogleCalendarSync push loop, so a deleted Google event
    // (a Dirty tombstone) also counts as work to upload.
    public static bool HasUploadWork(CalendarEvent ev)
        => ev.Dirty && !ev.LocalOnly && !ev.Conflict && !ev.RemoteReadOnly;

    public static bool HasPendingUploads(IEnumerable<CalendarEvent> events)
        => events.Any(HasUploadWork);

    public static CalendarPromptSyncDecision Decide(bool canSyncSilently, bool hasWork,
        bool connecting, bool editing, bool movingEvent, bool synchronizing)
    {
        if (!canSyncSilently || !hasWork) return CalendarPromptSyncDecision.Skip;
        if (connecting || editing || movingEvent || synchronizing) return CalendarPromptSyncDecision.RetryLater;
        return CalendarPromptSyncDecision.Run;
    }
}

// Short, consistent sync-state wording for the event bar and its tooltip.
public static class CalendarEventStatusText
{
    public const string PendingMarker = "⇡ ";
    public const string ConflictMarker = "⚠ ";

    public static bool IsPendingUpload(CalendarEvent ev)
        => ev.Dirty && !ev.LocalOnly && !ev.Conflict && !ev.RemoteReadOnly && !ev.Deleted;

    // googleConnected=false (no token, binding or calendar list on this PC): nothing is
    // uploading, so events made before the first connection show no upload marker.
    public static string BarPrefix(CalendarEvent ev, bool googleConnected = true)
        => ev.Conflict ? ConflictMarker : googleConnected && IsPendingUpload(ev) ? PendingMarker : "";

    public static string SyncState(CalendarEvent ev, bool googleConnected = true)
    {
        if (ev.Conflict)
            return string.IsNullOrWhiteSpace(ev.SyncError) ? "충돌" : "충돌 · " + ev.SyncError;
        if (ev.LocalOnly) return "로컬 전용";
        if (IsPendingUpload(ev)) return googleConnected ? "Google 업로드 대기" : "로컬 (Google 연결 전)";
        if (ev.RemoteReadOnly) return "읽기 전용";
        return ev.RemoteId is not null ? "Google 동기화됨" : "로컬 전용";
    }

    public static string FormatWhen(CalendarEvent ev)
    {
        if (ev.StartAt is { } start && ev.EndAt is { } end)
        {
            var from = start.ToLocalTime();
            var to = end.ToLocalTime();
            return from.Date == to.Date
                ? from.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "~"
                    + to.ToString("HH:mm", CultureInfo.InvariantCulture)
                : from.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " ~ "
                    + to.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        var first = ev.Date.Date;
        var last = ev.EffectiveEndDate.Date;
        return last <= first
            ? first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ~ "
                + last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // "제목 / 2026-03-02 09:00~10:30 / 캘린더 이름 · Google 업로드 대기"
    public static string Tooltip(CalendarEvent ev, string? calendarName, bool googleConnected = true)
    {
        var title = string.IsNullOrWhiteSpace(ev.Title) ? "(제목 없음)" : ev.Title;
        var source = string.IsNullOrWhiteSpace(calendarName) ? "" : calendarName + " · ";
        return title + "\n" + FormatWhen(ev) + "\n" + source + SyncState(ev, googleConnected);
    }
}

public sealed record GoogleDisconnectResult(int Removed, int ConvertedToLocal, int KeptLocal)
{
    public bool Revoked { get; init; }
}

// Disconnecting a Google account must not leave events that carry the old
// account's RemoteId/ETag: a later connection to another account would push them
// there. Downloaded copies are removed (Google keeps them); unsent local work is
// kept on this PC as local-only events with every remote field cleared.
public static class GoogleDisconnectPolicy
{
    public static int CountUnsentChanges(IEnumerable<CalendarEvent> events)
        => events.Count(e => !e.LocalOnly && !e.Deleted && (e.Dirty || e.RemoteCreatePending));

    public static int CountDownloadedCopies(IEnumerable<CalendarEvent> events)
        => events.Count(e => !e.LocalOnly && !e.Deleted && e.RemoteId is not null
            && !e.Dirty && !e.RemoteCreatePending);

    public static GoogleDisconnectResult Apply(List<CalendarEvent> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int removed = 0, converted = 0, kept = 0;
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            var ev = snapshot[i];
            if (ev.LocalOnly)
            {
                ClearRemoteFields(ev);
                kept++;
                continue;
            }
            if (ev.Deleted || (ev.RemoteId is not null && !ev.Dirty && !ev.RemoteCreatePending))
            {
                snapshot.RemoveAt(i);
                removed++;
                continue;
            }
            ClearRemoteFields(ev);
            converted++;
        }
        return new GoogleDisconnectResult(removed, converted, kept);
    }

    private static void ClearRemoteFields(CalendarEvent ev)
    {
        ev.CalendarId = CalendarEventStore.LocalCalendarId;
        ev.LocalOnly = true;
        ev.RemoteId = null;
        ev.RemoteETag = null;
        ev.RemoteCreatePending = false;
        ev.RemoteReadOnly = false;
        ev.Dirty = false;
        ev.Conflict = false;
        ev.SyncError = null;
    }
}
