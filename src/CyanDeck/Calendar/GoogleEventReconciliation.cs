using System.Globalization;
using System.Text.Json;

namespace CyanDeck.Calendar;

// Compare only the fields owned by CyanDeck, not Google-provided guests,
// conference details or description. Used to reconcile a timed-out POST whose
// user-supplied event ID already exists on Google (HTTP 409).
internal static class GoogleEventReconciliation
{
    public static bool MatchesLocalContent(JsonElement remote, CalendarEvent local)
    {
        if (!remote.TryGetProperty("summary", out var summary)
            || summary.ValueKind != JsonValueKind.String
            || !string.Equals(summary.GetString(), local.Title, StringComparison.Ordinal))
            return false;
        if (!remote.TryGetProperty("start", out var start)
            || !remote.TryGetProperty("end", out var end))
            return false;

        if (local.StartAt.HasValue != local.EndAt.HasValue) return false;
        return local.StartAt.HasValue
            ? MatchesTimed(start, local.StartAt.Value) && MatchesTimed(end, local.EndAt!.Value)
            : MatchesAllDay(start, local.Date.Date) && MatchesAllDay(end, local.EffectiveEndDate.AddDays(1));
    }

    // A full resync must refresh clean cached data even when the ETag is
    // identical. The user may have explicitly discarded a conflicting local
    // edit after a 409-recovery probe already checkpointed the remote ETag.
    public static bool ShouldApplyRemote(bool fullSync, CalendarEvent local,
        string? remoteETag, string sourceId, bool readOnly)
        => fullSync || local.Deleted || local.RemoteETag != remoteETag
            || local.CalendarId != sourceId || local.RemoteReadOnly != readOnly;

    private static bool MatchesAllDay(JsonElement point, DateTime day)
        => point.ValueKind == JsonValueKind.Object
            && point.TryGetProperty("date", out var value)
            && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && !point.TryGetProperty("dateTime", out _);

    private static bool MatchesTimed(JsonElement point, DateTimeOffset timestamp)
        => point.ValueKind == JsonValueKind.Object
            && point.TryGetProperty("dateTime", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var remote)
            && remote.ToUniversalTime() == timestamp.ToUniversalTime()
            && !point.TryGetProperty("date", out _);
}
