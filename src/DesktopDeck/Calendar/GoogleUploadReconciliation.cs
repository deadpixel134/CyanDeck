using System.Text.Json;

namespace DesktopDeck.Calendar;

// Pure reconciliation rules; exercised in the platform-independent domain harness.
// The server reply is authoritative only for fields owned by DesktopDeck.
internal static class GoogleUploadReconciliation
{
    internal enum Resolution { Clean, LocalEditPending, Conflict }

    public static bool SameOwnedFields(CalendarEvent a, CalendarEvent b)
        => string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && a.Date.Date == b.Date.Date
            && a.EffectiveEndDate == b.EffectiveEndDate
            && a.StartAt == b.StartAt && a.EndAt == b.EndAt
            && string.Equals(a.CalendarId, b.CalendarId, StringComparison.Ordinal)
            && a.Deleted == b.Deleted;

    public static Resolution Resolve(JsonElement remote, CalendarEvent sent, CalendarEvent current)
    {
        // Acknowledging a matching remote state is safe even after a timeout.
        if (!current.Deleted && GoogleEventReconciliation.MatchesLocalContent(remote, current))
            return Resolution.Clean;
        // UI edits made while an upload was outstanding must remain pending.
        // If the server still has what we sent, a subsequent PATCH is safe.
        if (!SameOwnedFields(sent, current)
            && GoogleEventReconciliation.MatchesLocalContent(remote, sent))
            return Resolution.LocalEditPending;
        // The server differs from both the sent and current revisions.
        return Resolution.Conflict;
    }
}
