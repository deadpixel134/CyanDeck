using System.Text.Json;

namespace CyanDeck.Calendar;

// Google refused to change ONE event (permission, invalid request, missing event).
// Only that event is marked as a conflict; the rest of the sync continues.
public sealed class GoogleEventRejectedException : Exception
{
    public GoogleEventRejectedException(int statusCode, string reason)
        : base(reason)
    {
        StatusCode = statusCode;
        Reason = reason;
    }

    public int StatusCode { get; }
    public string Reason { get; }
}

// The saved refresh token was revoked or expired (OAuth error=invalid_grant).
// Retrying the same token can never succeed: the user must sign in again.
public sealed class GoogleReauthRequiredException : Exception
{
    public GoogleReauthRequiredException()
        : base(GoogleSyncFailurePolicy.ReauthMessage) { }
}

// Pure classification of Google Calendar replies. No network, WPF or disk access,
// so the calendar domain harness exercises it directly.
public static class GoogleSyncFailurePolicy
{
    public const string ReauthMessage =
        "Google 로그인이 만료되었습니다. 캘린더 설정 → 'Google 계정 다시 연결'을 누르세요.";
    public const string RejectionPrefix = "Google이 수정을 거부함: ";
    public const string OrganizerOnlyDetail = "주최자만 수정할 수 있는 일정입니다";

    // 400/403/404/410/422 describe this event. 401, 408, 429 and 5xx describe the
    // connection, the account or the server, so the whole sync stops for those.
    public static bool IsPermanentEventRejection(int statusCode)
        => statusCode is 400 or 403 or 404 or 410 or 422;

    // Some 403 replies are about the whole account or project (missing scope, API
    // disabled). Marking every event as a conflict would hide the real problem.
    public static bool IsAccountWideFailure(string? body)
    {
        var reason = ReadErrorReason(body);
        return reason is "accessNotConfigured" or "insufficientPermissions"
            or "ACCESS_TOKEN_SCOPE_INSUFFICIENT" or "SERVICE_DISABLED" or "authError";
    }

    public static bool IsEventRejection(int statusCode, string? body)
        => IsPermanentEventRejection(statusCode) && !IsAccountWideFailure(body);

    public static string DescribeRejection(int statusCode, string? body)
    {
        var reason = ReadErrorReason(body);
        var detail = reason switch
        {
            "forbiddenForNonOrganizer" => OrganizerOnlyDetail,
            "requiredAccessLevel" or "forbidden" or "PERMISSION_DENIED"
                => "이 캘린더에서 이 일정을 수정할 권한이 없습니다",
            "notFound" or "deleted" or "NOT_FOUND"
                => "Google에서 이 일정을 찾을 수 없습니다(이미 삭제되었을 수 있음)",
            "invalid" or "badRequest" or "required" or "timeRangeEmpty" or "INVALID_ARGUMENT"
                => "Google이 일정 내용을 받아들이지 않았습니다",
            _ => statusCode switch
            {
                403 => "이 캘린더에서 이 일정을 수정할 권한이 없습니다",
                404 or 410 => "Google에서 이 일정을 찾을 수 없습니다(이미 삭제되었을 수 있음)",
                400 or 422 => "Google이 일정 내용을 받아들이지 않았습니다",
                _ => "HTTP " + statusCode
            }
        };
        return RejectionPrefix + detail;
    }

    // OAuth token endpoint: {"error":"invalid_grant", ...} on a revoked/expired refresh token.
    public static bool IsReauthorizationRequired(int statusCode, string? body)
    {
        if (statusCode is not (400 or 401) || string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
                return string.Equals(error.GetString(), "invalid_grant", StringComparison.Ordinal);
        }
        catch (JsonException) { }
        return body.Contains("\"invalid_grant\"", StringComparison.Ordinal);
    }

    // My copy of a meeting someone else organizes. Google rejects time/title changes
    // unless the organizer allowed guests to modify the event.
    public static bool IsInvitedAttendeeCopy(JsonElement item, string calendarId)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (item.TryGetProperty("guestsCanModify", out var guests) && guests.ValueKind == JsonValueKind.True)
            return false;
        if (!item.TryGetProperty("organizer", out var organizer) || organizer.ValueKind != JsonValueKind.Object)
            return false;
        if (organizer.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True)
            return false;
        if (organizer.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
            && string.Equals(email.GetString(), calendarId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!item.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var attendee in attendees.EnumerateArray())
            if (attendee.ValueKind == JsonValueKind.Object
                && attendee.TryGetProperty("self", out var me) && me.ValueKind == JsonValueKind.True)
                return true;
        return false;
    }

    // Google API errors: {"error":{"errors":[{"reason":"..."}],"status":"...","details":[{"reason":"..."}]}}
    public static string? ReadErrorReason(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
                return null;
            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var entry in errors.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(reason.GetString()))
                        return reason.GetString();
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var detail in details.EnumerateArray())
                    if (detail.ValueKind == JsonValueKind.Object
                        && detail.TryGetProperty("reason", out var detailReason) && detailReason.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(detailReason.GetString()))
                        return detailReason.GetString();
            if (error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                return status.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
