using CyanDeck.Services;

namespace CyanDeck.Calendar;

// Account lifecycle additions: silent-sync eligibility, sign-in expiry, per-event
// rejection bookkeeping and an explicit disconnect.
public sealed partial class GoogleCalendarSync
{
    // Set when Google answers a refresh with invalid_grant. The account binding and
    // the token file are kept; the next explicit connect opens the browser directly.
    private bool _needsReauthorization;

    public bool NeedsReauthorization => _needsReauthorization;

    // True only when a sync can run without opening a browser login: a saved token for
    // the current client, no sign-in expiry, no quota pause and writable stores.
    public bool CanSyncSilently
    {
        get
        {
            if (_accountBindingUnreadable) return false;
            if (DateTime.UtcNow < _quotaBlockedUntilUtc) return false;
            if (_store.IsReadOnly || _preferences.IsReadOnly) return false;
            return HasSilentToken();
        }
    }

    // Interval sync: same no-browser rule, but an unreadable account binding or a quota
    // pause still runs so SynchronizeAsync shows its existing message (no network for
    // the quota pause; the binding check fails before any remote write).
    public bool CanSyncInBackground => HasSilentToken();

    private bool HasSilentToken()
    {
        if (_disposed || _needsReauthorization) return false;
        var token = _token;
        if (token is null) return false;
        string id;
        try { id = _clientId().Trim(); }
        catch (Exception) { return false; }
        if (id.Length == 0 || !string.Equals(token.ClientId, id, StringComparison.Ordinal)) return false;
        return token.RefreshToken.Length > 0 || DateTime.UtcNow < token.ExpiresUtc;
    }

    // Anything that ties this PC to a Google account: a token, an account binding or a catalog.
    public bool HasAccountConnection
        => _token is not null || _boundCalendarId is not null || _accountBindingUnreadable
            || _preferences.Data.Catalog.Count > 0 || File.Exists(_tokenPath) || File.Exists(_accountPath);

    // Once per preferences file: drop the sync tokens so the next pull is a full resync
    // that recomputes RemoteReadOnly for unchanged meetings organised by someone else.
    // Both values change in one checkpoint; a failed pull just leaves a full resync due.
    private void EnsureInvitedMeetingResync()
    {
        if (_preferences.Data.InvitedMeetingsResynced) return;
        _preferences.CommitUpdate(next =>
        {
            next.SyncTokens.Clear();
            next.InvitedMeetingsResynced = true;
        });
    }

    private static string DescribeRejectedUploads(int rejected)
        => rejected > 0 ? " · Google이 거부한 일정 " + rejected + "건(⚠ 메뉴에서 이유 확인·복원)" : "";

    private static async Task ThrowIfEventRejectedAsync(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (!GoogleSyncFailurePolicy.IsPermanentEventRejection(status)) return;
        var body = await response.Content.ReadAsStringAsync();
        if (!GoogleSyncFailurePolicy.IsEventRejection(status, body)) return;
        DiagnosticsLog.Warn($"Google rejected one calendar event upload: HTTP {status} "
            + (GoogleSyncFailurePolicy.ReadErrorReason(body) ?? "unknown"));
        throw new GoogleEventRejectedException(status, GoogleSyncFailurePolicy.DescribeRejection(status, body));
    }

    // Mark only this event; it is skipped by later syncs until the user restores the
    // Google version or retries from the ⚠ menu. A refused deletion is shown again so
    // the user can see and resolve it instead of it staying hidden forever. That also
    // covers a pending create whose pre-delete check was refused: the push loop skips
    // conflicts, so a hidden conflicted tombstone could never be resolved.
    private void MarkUploadRejected(Guid id, string reason)
    {
        _store.CommitEventChange(id, current =>
        {
            current.Conflict = true;
            current.SyncError = reason;
            // A refused "모든 반복 일정" delete is undone like other series conflicts, so a later
            // retry cannot turn it into a series title/time change.
            if (CalendarRecurrenceExpander.UndoConflictedSeriesDelete(current)) return true;
            if (current.Deleted && current.RemoteId is not null)
                current.Deleted = false;
            return true;
        });
    }

    // Explicit "Google 연결 해제…": revoke (best effort), clear the account's calendar
    // list and sync state, drop downloaded Google copies, keep unsent work as local-only
    // events, then delete the token and the account binding. Runs under the sync gate
    // so it can never overlap an upload.
    public async Task<GoogleDisconnectResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GoogleCalendarSync));
        if (!await _gate.WaitAsync(0, _shutdown.Token))
            throw new InvalidOperationException("Google 동기화가 진행 중입니다. 끝난 뒤 다시 시도하세요.");
        try
        {
            if (_store.IsReadOnly || _preferences.IsReadOnly)
                throw new IOException("캘린더 저장소가 읽기 전용이라 연결을 해제할 수 없습니다.");
            var revoked = await TryRevokeAsync(_token?.RefreshToken is { Length: > 0 } refresh
                ? refresh : _token?.AccessToken, cancellationToken);
            // Sync tokens first: if a later step fails, the next sync is a full resync
            // instead of an incremental one that would skip removed events.
            _preferences.ClearGoogleAccount();
            var snapshot = _store.CreateSnapshot();
            var result = GoogleDisconnectPolicy.Apply(snapshot);
            _store.CommitSnapshot(snapshot);
            DeleteAccountFile(_tokenPath);
            DeleteAccountFile(_tokenPath + ".tmp");
            DeleteAccountFile(_accountPath);
            DeleteAccountFile(_accountPath + ".tmp");
            _credentials.Clear();
            _boundCalendarId = null;
            _accountBindingUnreadable = false;
            _needsReauthorization = false;
            DiagnosticsLog.Info($"Google Calendar disconnected: removed={result.Removed}, "
                + $"keptAsLocal={result.ConvertedToLocal}, local={result.KeptLocal}, revoked={revoked}");
            return result with { Revoked = revoked };
        }
        finally { _gate.Release(); }
    }

    private static void DeleteAccountFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private async Task<bool> TryRevokeAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.PostAsync("https://oauth2.googleapis.com/revoke",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Local disconnect continues; the user can also remove access in Google account settings.
            DiagnosticsLog.Warn("Google token revoke skipped: " + ex.Message);
            return false;
        }
    }
}
