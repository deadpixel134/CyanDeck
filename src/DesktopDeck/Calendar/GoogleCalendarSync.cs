using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopDeck.Services;

namespace DesktopDeck.Calendar;

// OAuth2/PKCE with account-bound multi-calendar synchronization.
// Google event times use exclusive ends; the local display model uses inclusive dates.
public sealed class GoogleCalendarSync : IDisposable
{
    private readonly CalendarEventStore _store;
    private readonly CalendarPreferencesStore _preferences;
    private readonly Func<string> _clientId;
    private readonly string _tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopDeck", "calendar-google-token.dat");
    private readonly string _accountPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopDeck", "calendar-google-owner.dat");
    private string? _boundCalendarId;
    private bool _accountBindingUnreadable;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // A candidate OAuth credential is usable for account verification but is
    // not saved until it matches the existing Google account binding.
    private readonly VerifiedCredentialCheckpoint<GoogleToken> _credentials = new();
    private GoogleToken? _token => _credentials.Active;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;
    private DateTime _quotaBlockedUntilUtc = DateTime.MinValue;
    private int _quotaFailures;
    private int _syncApiRequests;

    private sealed class GoogleToken
    {
        public string ClientId { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }
    }

    public bool HasCachedToken => _token is not null;

    public GoogleCalendarSync(CalendarEventStore store, CalendarPreferencesStore preferences, Func<string> clientId)
    {
        _store = store;
        _preferences = preferences;
        _clientId = clientId;
        try
        {
            if (File.Exists(_tokenPath))
                _credentials.LoadVerified(JsonSerializer.Deserialize<GoogleToken>(
                    Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(_tokenPath)))));
        }
        catch (Exception ex) { DiagnosticsLog.Error("Google credential loading failed", ex); }
        try
        {
            if (File.Exists(_accountPath))
                _boundCalendarId = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(_accountPath)));
        }
        catch (Exception ex)
        {
            _accountBindingUnreadable = true;
            DiagnosticsLog.Error("Google account binding unreadable; remote changes disabled", ex);
        }
    }

    public async Task<string> SynchronizeAsync()
    {
        if (_disposed) return "Google 동기화 기능이 종료되었습니다.";
        try
        {
            if (!await _gate.WaitAsync(0, _shutdown.Token)) return "Google 동기화가 이미 진행 중입니다.";
        }
        catch (OperationCanceledException) { return "Google 동기화가 취소되었습니다."; }
        try
        {
            if (_store.IsReadOnly || _preferences.IsReadOnly) return "캘린더 저장소가 읽기 전용입니다.";
            if (DateTime.UtcNow < _quotaBlockedUntilUtc)
                return QuotaWaitMessage();
            _syncApiRequests = 0;
            var id = _clientId().Trim();
            if (id.Length == 0) return "설정에서 Google OAuth 데스크톱 클라이언트 ID를 입력하세요.";
            if (_token is null || _token.ClientId != id) await AuthorizeAsync(id);
            await RefreshIfNeededAsync(id);
            await VerifyAccountBindingAsync(); // before ANY remote write
            if (GoogleCalendarCatalogRefreshPolicy.ShouldRefresh(_preferences.Data, DateTime.UtcNow))
                await RefreshCalendarListCoreAsync();
            var pushed = 0;
            var conflicts = 0;
            var pending = 0;
            foreach (var ev in _store.Events.Where(x => x.Dirty && !x.LocalOnly && !x.Conflict && !x.RemoteReadOnly).ToArray())
            {
                var outcome = await PushAsync(ev);
                if (outcome == "conflict") conflicts++;
                else if (outcome == "ok") pushed++;
                else if (outcome == "pending") pending++;
            }
            var pulled = await PullAsync();
            _store.Save();
            _quotaFailures = 0;
            _quotaBlockedUntilUtc = DateTime.MinValue;
            return $"Google 동기화 완료 · 업로드 {pushed} · 가져오기/갱신 {pulled} · 충돌 {conflicts} · 로컬 재업로드 대기 {pending} · API 요청 {_syncApiRequests}회";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return "Google 동기화가 중지되었습니다.";
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Google Calendar sync failed", ex);
            return "Google 동기화 오류: " + ex.Message;
        }
        finally { _gate.Release(); }
    }

    // Explicit list refresh from the in-desktop calendar preferences panel.
    // Explicit user action: cancellation covers semaphore wait, the browser callback,
    // token exchange and subsequent calendar API calls. A failed attempt must not
    // leave a candidate credential active or block the next click.
    public async Task<IReadOnlyList<GoogleCalendarSource>> LoadCalendarsAsync(
        CancellationToken cancellationToken = default, bool forceReauthorize = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GoogleCalendarSync));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token);
        try
        {
            if (_store.IsReadOnly || _preferences.IsReadOnly)
                throw new IOException("캘린더 저장소가 읽기 전용입니다.");
            if (DateTime.UtcNow < _quotaBlockedUntilUtc)
                throw new InvalidOperationException(QuotaWaitMessage());
            var id = _clientId().Trim();
            if (id.Length == 0) throw new InvalidOperationException("설정에서 Google OAuth 클라이언트 ID를 입력하세요.");
            if (forceReauthorize || _token is null || _token.ClientId != id)
                await AuthorizeAsync(id, token);
            token.ThrowIfCancellationRequested();
            await RefreshIfNeededAsync(id, token);
            await VerifyAccountBindingAsync(token);
            return await RefreshCalendarListCoreAsync(token);
        }
        catch
        {
            // Account-affinity verification and the on-disk token are retained.
            // Reject only the *unverified* credential from a failed login.
            DiscardUnverifiedToken();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<GoogleCalendarSource>> RefreshCalendarListCoreAsync(CancellationToken cancellationToken = default)
    {
        var calendars = new List<GoogleCalendarSource>();
        string? pageToken = null;
        do
        {
            var query = new Dictionary<string, string> { ["maxResults"] = "250", ["showHidden"] = "true" };
            if (pageToken is not null) query["pageToken"] = pageToken;
            using var response = await SendApiAsync(HttpMethod.Get, "users/me/calendarList?" + FormValues(query), cancellationToken: cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                calendars.Add(new GoogleCalendarSource
                {
                    Id = id,
                    Name = item.TryGetProperty("summary", out var name) ? name.GetString() ?? id : id,
                    Color = item.TryGetProperty("backgroundColor", out var color) ? color.GetString() ?? "#5D91DD" : "#5D91DD",
                    AccessRole = item.TryGetProperty("accessRole", out var role) ? role.GetString() ?? "reader" : "reader",
                    IsPrimary = item.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True,
                    SelectedInGoogle = item.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True,
                    HiddenInGoogle = item.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True
                });
            }
            pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (pageToken is not null);
        // A single account can contain many primary, secondary and subscribed
        // calendars. Keep all pages, then present primary before others.
        var ordered = calendars.OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        _preferences.UpdateCatalog(ordered);
        return ordered;
    }

    private async Task VerifyAccountBindingAsync(CancellationToken cancellationToken = default)
    {
        if (_accountBindingUnreadable)
            throw new InvalidOperationException("Google 계정 연결 정보를 복호화할 수 없어 동기화를 중단했습니다. 원본 파일을 백업하고 복구하세요.");

        using var response = await SendApiAsync(HttpMethod.Get, "users/me/calendarList/primary", cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var current = json.RootElement.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(current))
            throw new InvalidOperationException("현재 Google 기본 캘린더 식별자를 확인할 수 없습니다.");
        if (_boundCalendarId is not null)
        {
            if (!string.Equals(_boundCalendarId, current, StringComparison.Ordinal))
            {
                DiscardUnverifiedToken();
                throw new InvalidOperationException("이 로컬 캘린더는 다른 Google 계정에 연결되어 있습니다. 잘못된 계정에 쓰지 않도록 동기화를 중단했습니다.");
            }
            PersistVerifiedToken();
            return;
        }

        // A previous prototype did not store account affinity. Validate at least
        // one previously synced remote resource (including ETag) before adopting.
        var oldLinked = _store.Events.Where(e => e.RemoteId is not null && !e.RemoteCreatePending).ToArray();
        if (oldLinked.Length > 0)
        {
            var verified = false;
            foreach (var ev in oldLinked.Where(e => !string.IsNullOrEmpty(e.RemoteETag)).Take(5))
            {
                // Older users can already have events from secondary calendars.
                // Probing 'primary' for every event incorrectly rejects the
                // correct account when all linked events live elsewhere.
                var legacyCalendarId = ev.CalendarId is null or "primary" ? "primary" : ev.CalendarId;
                using var probe = await SendApiAsync(HttpMethod.Get, "calendars/"
                    + Uri.EscapeDataString(legacyCalendarId) + "/events/" + Uri.EscapeDataString(ev.RemoteId!),
                    cancellationToken: cancellationToken);
                if (!probe.IsSuccessStatusCode) continue;
                using var found = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
                var tag = found.RootElement.TryGetProperty("etag", out var etag) ? etag.GetString() : null;
                if (string.Equals(tag, ev.RemoteETag, StringComparison.Ordinal)) { verified = true; break; }
            }
            if (!verified)
            {
                DiscardUnverifiedToken();
                throw new InvalidOperationException("이전 버전의 Google 연결 계정을 안전하게 확인하지 못했습니다. 원격 데이터 보호를 위해 자동 연결하지 않습니다.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Bind before uploading any local-only events. DPAPI protects the account
        // identifier and prevents a later account switch from targeting wrong IDs.
        Directory.CreateDirectory(Path.GetDirectoryName(_accountPath)!);
        var temp = _accountPath + ".tmp";
        File.WriteAllBytes(temp, Protect(Encoding.UTF8.GetBytes(current)));
        File.Move(temp, _accountPath, overwrite: false);
        _boundCalendarId = current;
        PersistVerifiedToken();
    }

    // Keeping an unverified access token in RAM is sufficient to check the
    // account ID. Do not replace the durable refresh token before this check:
    // an OAuth login to another Google account must not strand the user.
    private void PersistVerifiedToken() => _credentials.PersistVerified(SaveToken);

    private void StageUnverifiedToken(GoogleToken candidate) => _credentials.Stage(candidate);

    private void DiscardUnverifiedToken() => _credentials.Reject();

    private async Task AuthorizeAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var tempSocket = new TcpListener(IPAddress.Loopback, 0);
        tempSocket.Start();
        var port = ((IPEndPoint)tempSocket.LocalEndpoint).Port;
        tempSocket.Stop();
        var redirect = $"http://127.0.0.1:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        var scopes = "https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.calendarlist.readonly";
        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + FormValues(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirect,
            ["response_type"] = "code", ["scope"] = scopes,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state
        });
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3),
            cancellationToken.CanBeCanceled ? cancellationToken : _shutdown.Token);
        var query = context.Request.QueryString;
        // Browser approval is not the same as a fully verified account binding.
        // The WPF caller confirms permissions, account identity and calendar catalog
        // only after the callback and token exchange have completed successfully.
        var message = query["error"] is string error ? "Google 인증 취소: " + error
            : "Google 권한 승인을 받았습니다. CyanDeck에서 계정 연결 결과를 확인하세요.";
        var page = Encoding.UTF8.GetBytes("<!doctype html><html><meta charset='utf-8'><body>" + WebUtility.HtmlEncode(message) + "</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page);
        context.Response.Close();
        if (query["state"] != state) throw new InvalidOperationException("OAuth state mismatch");
        var code = query["code"];
        if (string.IsNullOrEmpty(code)) throw new InvalidOperationException(message);
        var result = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code"
        }, cancellationToken);
        var refreshToken = result.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString() : null;
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Google에서 오프라인 액세스 토큰을 반환하지 않았습니다. 계정 연결을 다시 승인하세요.");
        var candidate = new GoogleToken
        {
            ClientId = clientId,
            AccessToken = result.GetProperty("access_token").GetString() ?? "",
            RefreshToken = refreshToken,
            ExpiresUtc = DateTime.UtcNow.AddSeconds(result.GetProperty("expires_in").GetInt32() - 90)
        };
        // A new account is not trusted until VerifyAccountBindingAsync succeeds.
        cancellationToken.ThrowIfCancellationRequested();
        StageUnverifiedToken(candidate);
    }

    private async Task RefreshIfNeededAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_token is null) throw new InvalidOperationException("Google 계정 인증이 필요합니다.");
        if (DateTime.UtcNow < _token.ExpiresUtc) return;
        if (_token.RefreshToken.Length == 0) { await AuthorizeAsync(id, cancellationToken); return; }
        var result = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = id, ["refresh_token"] = _token.RefreshToken, ["grant_type"] = "refresh_token"
        }, cancellationToken);
        // Stage the refreshed values without mutating the previously verified
        // token. Persistence occurs after successful account-affinity check.
        var candidate = new GoogleToken
        {
            ClientId = id,
            RefreshToken = _token.RefreshToken,
            AccessToken = result.GetProperty("access_token").GetString() ?? "",
            ExpiresUtc = DateTime.UtcNow.AddSeconds(result.GetProperty("expires_in").GetInt32() - 90)
        };
        cancellationToken.ThrowIfCancellationRequested();
        StageUnverifiedToken(candidate);
    }

    private async Task<JsonElement> PostTokenAsync(Dictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        // Both authorization-code exchange and refresh-token exchange use the
        // same installed-app credentials. PKCE is retained on initial sign-in.
        if (values.TryGetValue("client_id", out var clientId))
        {
            var clientSecret = GoogleOAuthClientConfiguration.ResolveSecret(clientId);
            if (clientSecret.Length > 0) values["client_secret"] = clientSecret;
        }
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values),
            cancellationToken.CanBeCanceled ? cancellationToken : _shutdown.Token);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if (body.Contains("client_secret is missing", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("앱 배포본에 Google 데스크톱 OAuth 인증 정보가 누락되었습니다. "
                    + "앱 제작자가 OAuth 설정이 포함된 새 버전을 빌드해야 합니다. 일반 사용자는 JSON을 입력할 필요가 없습니다. "
                    + "(개발자 설정 파일: " + GoogleOAuthClientConfiguration.InstalledClientFile + ")");
            throw new HttpRequestException($"Google OAuth {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string url, object? payload = null,
        string? etag = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, "https://www.googleapis.com/calendar/v3/" + url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token!.AccessToken);
        if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request,
            cancellationToken.CanBeCanceled ? cancellationToken : _shutdown.Token);
        _syncApiRequests++;
        // 403 can also mean permission errors: throttle ONLY confirmed quota reasons.
        // Do not retry automatically: another immediate request wastes project quota.
        var quotaReason = response.StatusCode == HttpStatusCode.Forbidden
            ? await response.Content.ReadAsStringAsync() : "";
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden && IsQuotaError(quotaReason)))
        {
            _quotaFailures = Math.Min(_quotaFailures + 1, 7);
            var backoff = quotaReason.Contains("dailyLimitExceeded", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromHours(24)
                : TimeSpan.FromMinutes(Math.Min(60, 5 * (1 << (_quotaFailures - 1))));
            if (response.Headers.RetryAfter?.Delta is { } retryAfter && retryAfter > backoff)
                backoff = retryAfter;
            if (response.Headers.RetryAfter?.Date is { } retryAt
                && retryAt - DateTimeOffset.UtcNow > backoff)
                backoff = retryAt - DateTimeOffset.UtcNow;
            _quotaBlockedUntilUtc = DateTime.UtcNow.Add(backoff);
            DiagnosticsLog.Warn($"Google Calendar API quota backoff until {_quotaBlockedUntilUtc:O}");
            response.Dispose();
            throw new HttpRequestException("Google Calendar API 요청 제한: " + QuotaWaitMessage());
        }
        return response;
    }

    private static bool IsQuotaError(string body)
        => body.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || body.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || body.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase)
            || body.Contains("dailyLimitExceeded", StringComparison.OrdinalIgnoreCase);

    private string QuotaWaitMessage()
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((_quotaBlockedUntilUtc - DateTime.UtcNow).TotalMinutes));
        return $"Google API 할당량 제한으로 {minutes}분 뒤 재시도할 수 있습니다. 자동 재요청은 잠시 중단합니다.";
    }

    private string ResolveCalendarId(CalendarEvent ev)
    {
        // Legacy events were always stored in primary. Resolve only after account validation.
        return ev.CalendarId is null or "primary" ? _boundCalendarId ?? "primary" : ev.CalendarId;
    }

    private static Dictionary<string, string> StartBody(CalendarEvent ev)
        => ev.StartAt is { } start
            ? new() { ["dateTime"] = start.ToString("O") }
            : new() { ["date"] = ev.Date.Date.ToString("yyyy-MM-dd") };

    private static Dictionary<string, string> EndBody(CalendarEvent ev)
        => ev.EndAt is { } end
            ? new() { ["dateTime"] = end.ToString("O") }
            : new() { ["date"] = ev.EffectiveEndDate.AddDays(1).ToString("yyyy-MM-dd") };

    // Explicit null removes the opposite field when converting between timed
    // and all-day Google events. Otherwise PATCH could retain both date/dateTime.
    private static Dictionary<string, string?> PatchTimeBody(CalendarEvent ev, bool start)
    {
        if (ev.StartAt.HasValue)
            return new Dictionary<string, string?>
            {
                ["date"] = null,
                ["dateTime"] = (start ? ev.StartAt!.Value : ev.EndAt!.Value).ToString("O")
            };
        return new Dictionary<string, string?>
        {
            ["date"] = start ? ev.Date.Date.ToString("yyyy-MM-dd")
                             : ev.EffectiveEndDate.AddDays(1).ToString("yyyy-MM-dd"),
            ["dateTime"] = null
        };
    }

    // Stage each upload checkpoint in a detached snapshot. A failed disk write
    // must leave both the public event collection and the previous file intact.
    private void MarkUploadConflict(Guid id)
    {
        _store.CommitEventChange(id, current =>
        {
            current.Conflict = true;
            return true;
        });
    }

    private void CheckpointRemoteReply(Guid id, CalendarEvent sent, JsonElement remote, bool create)
    {
        var remoteId = remote.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        var remoteTag = remote.TryGetProperty("etag", out var tagProperty) ? tagProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(remoteId) || string.IsNullOrWhiteSpace(remoteTag)
            || (!string.IsNullOrWhiteSpace(sent.RemoteId) && remoteId != sent.RemoteId)
            || (create && !MatchesPrivateId(remote, sent)))
            throw new InvalidOperationException("Google ACK에 이벤트 ID 또는 ETag/소유자 정보가 없어 로컬 변경사항을 보존합니다.");

        _store.CommitEventChange(id, current =>
        {
            var resolution = GoogleUploadReconciliation.Resolve(remote, sent, current);
            current.RemoteId = remoteId;
            current.RemoteETag = remoteTag;
            current.RemoteCreatePending = false;
            current.Dirty = resolution != GoogleUploadReconciliation.Resolution.Clean;
            current.Conflict = resolution == GoogleUploadReconciliation.Resolution.Conflict;
            return true;
        });
    }

    private async Task<string> RecoverRemoteVersionAsync(Guid id, CalendarEvent sent, string remoteUrl, bool create)
    {
        using var probe = await SendApiAsync(HttpMethod.Get, remoteUrl);
        if (!probe.IsSuccessStatusCode)
        {
            MarkUploadConflict(id);
            return "conflict";
        }
        using var document = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
        if (create && !MatchesPrivateId(document.RootElement, sent))
        {
            MarkUploadConflict(id);
            return "conflict";
        }
        CheckpointRemoteReply(id, sent, document.RootElement, create);
        var current = _store.Events.FirstOrDefault(e => e.Id == id);
        return UploadOutcome(current);
    }

    private static string UploadOutcome(CalendarEvent? current)
        => current?.Conflict == true ? "conflict"
            : current?.Dirty == true ? "pending" : "ok";

    private async Task<string> PushAsync(CalendarEvent selected)
    {
        // Selected objects may be stale after a snapshot has been published.
        var ev = _store.Events.FirstOrDefault(x => x.Id == selected.Id);
        if (ev is null || !ev.Dirty || ev.LocalOnly || ev.Conflict || ev.RemoteReadOnly) return "skip";
        var id = ev.Id;
        var calendarId = ResolveCalendarId(ev);
        if (!_preferences.Data.Catalog.Any(c => c.Id == calendarId && c.CanWrite))
        {
            MarkUploadConflict(id);
            return "conflict";
        }
        if (!ev.Deleted && ev.RemoteId is null)
        {
            // Durable client-generated identity BEFORE the first POST. If the
            // request succeeds but the ACK is lost, GET/409 can resolve it.
            _store.CommitEventChange(id, staged =>
            {
                staged.CalendarId = calendarId;
                staged.RemoteId = "dd" + staged.Id.ToString("N");
                staged.RemoteCreatePending = true;
                return true;
            });
            ev = _store.Events.First(x => x.Id == id);
        }
        if (ev.Deleted && ev.RemoteId is null)
        {
            _store.CommitEventRemoval(id);
            return "ok";
        }
        var remoteUrl = "calendars/" + Uri.EscapeDataString(calendarId)
            + "/events/" + Uri.EscapeDataString(ev.RemoteId!);
        if (!ev.RemoteCreatePending && string.IsNullOrWhiteSpace(ev.RemoteETag))
        {
            MarkUploadConflict(id);
            return "conflict";
        }

        if (ev.Deleted && ev.RemoteCreatePending)
        {
            // A pending POST may have succeeded. Probe before deleting;
            // never send an unconditional DELETE without an ETag.
            using var probe = await SendApiAsync(HttpMethod.Get, remoteUrl);
            if (probe.StatusCode == HttpStatusCode.NotFound)
            {
                // The preceding POST might still be propagating across Google.
                // Preserve the local tombstone and recheck on a later sync.
                // An early 404 is not proof that the remote creation never ran.
                return "pending";
            }
            probe.EnsureSuccessStatusCode();
            using var found = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
            var tag = found.RootElement.TryGetProperty("etag", out var remoteTag) ? remoteTag.GetString() : null;
            if (!MatchesPrivateId(found.RootElement, ev) || string.IsNullOrEmpty(tag))
            {
                MarkUploadConflict(id);
                return "conflict";
            }
            _store.CommitEventChange(id, staged =>
            {
                staged.RemoteETag = tag;
                staged.RemoteCreatePending = false;
                return true;
            });
            ev = _store.Events.First(x => x.Id == id);
        }

        // Snapshot the payload *before* awaiting network I/O. It is used to
        // distinguish a later local edit from a genuine remote conflict.
        var sent = _store.CreateSnapshot().First(x => x.Id == id);
        HttpResponseMessage response;
        if (sent.Deleted)
        {
            response = await SendApiAsync(HttpMethod.Delete, remoteUrl, etag: sent.RemoteETag);
        }
        else if (sent.RemoteCreatePending)
        {
            var insert = new
            {
                id = sent.RemoteId, summary = sent.Title,
                start = StartBody(sent), end = EndBody(sent),
                extendedProperties = new
                {
                    @private = new Dictionary<string, string> { ["desktopDeckLocalId"] = sent.Id.ToString("N") }
                }
            };
            response = await SendApiAsync(HttpMethod.Post,
                "calendars/" + Uri.EscapeDataString(calendarId) + "/events", insert);
        }
        else
        {
            // PATCH only owned fields; retain guests, descriptions and Meet links.
            var patch = new { summary = sent.Title, start = PatchTimeBody(sent, true), end = PatchTimeBody(sent, false) };
            response = await SendApiAsync(HttpMethod.Patch, remoteUrl, patch, sent.RemoteETag);
        }

        using (response)
        {
            if (sent.RemoteCreatePending && response.StatusCode == HttpStatusCode.Conflict)
                return await RecoverRemoteVersionAsync(id, sent, remoteUrl, create: true);

            // If a previous PATCH completed remotely, the stale If-Match tag
            // produces 412 on retry. GET reconciles the desired owned fields;
            // otherwise preserve the local edit as a conflict.
            if (!sent.Deleted && !sent.RemoteCreatePending
                && response.StatusCode == HttpStatusCode.PreconditionFailed)
                return await RecoverRemoteVersionAsync(id, sent, remoteUrl, create: false);

            if (response.StatusCode == HttpStatusCode.PreconditionFailed || response.StatusCode == HttpStatusCode.Conflict)
            {
                MarkUploadConflict(id);
                return "conflict";
            }
            if ((response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
                && sent.Deleted)
            {
                _store.CommitEventRemoval(id);
                return "ok";
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Google 이벤트 업데이트 실패 {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            if (sent.Deleted)
            {
                _store.CommitEventRemoval(id);
                return "ok";
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            CheckpointRemoteReply(id, sent, document.RootElement, sent.RemoteCreatePending);
            var current = _store.Events.FirstOrDefault(e => e.Id == id);
            return UploadOutcome(current);
        }
    }

    private static bool MatchesPrivateId(JsonElement root, CalendarEvent ev)
        => root.TryGetProperty("extendedProperties", out var properties)
            && properties.TryGetProperty("private", out var privateProperties)
            && privateProperties.TryGetProperty("desktopDeckLocalId", out var marker)
            && string.Equals(marker.GetString(), ev.Id.ToString("N"), StringComparison.Ordinal);

    private async Task<int> PullAsync()
    {
        var imported = 0;
        // One sync token per calendar. Never infer deletion from an unselected
        // calendar, and never delete unsent local edits when a token expires.
        var selected = _preferences.Data.SelectedCalendarIds ?? [];
        foreach (var source in _preferences.Data.Catalog.Where(x => selected.Contains(x.Id)))
        {
            _preferences.Data.SyncTokens.TryGetValue(source.Id, out var syncToken);
            var completed = false;
            for (var attempt = 0; attempt < 2 && !completed; attempt++)
            {
                string? pageToken = null;
                string? nextSyncToken = null;
                var fullSync = string.IsNullOrWhiteSpace(syncToken);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var retryFull = false;
                do
                {
                    var values = new Dictionary<string, string>
                    {
                        ["singleEvents"] = "true", ["showDeleted"] = "true", ["maxResults"] = "2500"
                    };
                    if (!fullSync) values["syncToken"] = syncToken!;
                    if (pageToken is not null) values["pageToken"] = pageToken;
                    using var response = await SendApiAsync(HttpMethod.Get,
                        "calendars/" + Uri.EscapeDataString(source.Id) + "/events?" + FormValues(values));
                    if (response.StatusCode == HttpStatusCode.Gone && !fullSync)
                    {
                        _preferences.UpdateSyncToken(source.Id, null);
                        syncToken = null;
                        retryFull = true;
                        break;
                    }
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"Google 일정 조회 실패 {source.Name} {(int)response.StatusCode}");
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    // Apply each page against a detached copy. The previous code
                    // mutated live objects before _store.Save(), so an I/O
                    // failure could leave RAM ahead of the on-disk checkpoint.
                    var pageSnapshot = _store.CreateSnapshot();
                    foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
                    {
                        var remoteId = item.TryGetProperty("id", out var id) ? id.GetString() : null;
                        if (string.IsNullOrWhiteSpace(remoteId)) continue;
                        var local = pageSnapshot.FirstOrDefault(e => e.RemoteId == remoteId
                            && string.Equals(ResolveCalendarId(e), source.Id, StringComparison.Ordinal));
                        var deleted = item.TryGetProperty("status", out var status) && status.GetString() == "cancelled";
                        var etag = item.TryGetProperty("etag", out var tag) ? tag.GetString() : null;
                        if (deleted)
                        {
                            if (local is not null)
                            {
                                if (local.Dirty) local.Conflict = true;
                                else local.Deleted = true;
                            }
                            continue;
                        }
                        seen.Add(remoteId);
                        if (!item.TryGetProperty("start", out var start)) continue;
                        if (!item.TryGetProperty("end", out var end)) continue;
                        var allDay = start.TryGetProperty("date", out var dateProp);
                        DateTime date;
                        DateTime endDate;
                        DateTimeOffset? startAt = null, endAt = null;
                        if (allDay)
                        {
                            if (!DateTime.TryParseExact(dateProp.GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out date)) continue;
                            if (!end.TryGetProperty("date", out var endDay)
                                || !DateTime.TryParseExact(endDay.GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.None, out var exclusiveEnd)) continue;
                            endDate = exclusiveEnd.AddDays(-1);
                        }
                        else
                        {
                            if (!start.TryGetProperty("dateTime", out var begin)
                                || !end.TryGetProperty("dateTime", out var finish)
                                || !DateTimeOffset.TryParse(begin.GetString(), out var beginAt)
                                || !DateTimeOffset.TryParse(finish.GetString(), out var finishAt)) continue;
                            startAt = beginAt; endAt = finishAt;
                            date = beginAt.ToLocalTime().Date;
                            endDate = finishAt.ToLocalTime().AddTicks(-1).Date;
                        }
                        if (endDate < date) endDate = date;
                        var recurring = item.TryGetProperty("recurringEventId", out _) || item.TryGetProperty("recurrence", out _);
                        var specialType = item.TryGetProperty("eventType", out var type)
                            && type.GetString() is not null and not "default";
                        var readOnly = !source.CanWrite || recurring || specialType
                            || (item.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.True);
                        var title = item.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "(제목 없음)" : "(제목 없음)";
                        if (local is null)
                        {
                            pageSnapshot.Add(new CalendarEvent
                            {
                                Date = date, EndDate = endDate, StartAt = startAt, EndAt = endAt,
                                Title = title, CalendarId = source.Id, RemoteId = remoteId,
                                RemoteETag = etag, Dirty = false, RemoteReadOnly = readOnly
                            });
                            imported++;
                        }
                        else if (local.Dirty)
                        {
                            if (!string.Equals(local.RemoteETag, etag, StringComparison.Ordinal)) local.Conflict = true;
                        }
                        else if (GoogleEventReconciliation.ShouldApplyRemote(fullSync, local, etag, source.Id, readOnly))
                        {
                            local.Title = title; local.Date = date; local.EndDate = endDate;
                            local.StartAt = startAt; local.EndAt = endAt;
                            local.CalendarId = source.Id; local.RemoteETag = etag;
                            local.Deleted = false; local.RemoteReadOnly = readOnly;
                            imported++;
                        }
                    }
                    pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                    if (pageToken is null && json.RootElement.TryGetProperty("nextSyncToken", out var newToken))
                        nextSyncToken = newToken.GetString();
                    _store.CommitSnapshot(pageSnapshot); // Atomic page checkpoint + publish.
                } while (pageToken is not null);

                if (retryFull) continue;
                if (string.IsNullOrEmpty(nextSyncToken))
                    throw new InvalidOperationException("Google에서 증분 동기화 토큰을 받지 못했습니다. 기존 변경사항은 보존합니다.");
                if (fullSync)
                {
                    // Only a COMPLETE unbounded full listing can determine absence.
                    // Never discard dirty/pending events when resynchronizing after 410.
                    var completedSnapshot = _store.CreateSnapshot();
                    foreach (var local in completedSnapshot.Where(e => e.RemoteId is not null
                        && !e.Dirty && !e.RemoteCreatePending && ResolveCalendarId(e) == source.Id
                        && !seen.Contains(e.RemoteId)).ToArray())
                        local.Deleted = true;
                    _store.CommitSnapshot(completedSnapshot);
                }
                _preferences.UpdateSyncToken(source.Id, nextSyncToken);
                completed = true;
            }
            if (!completed) throw new InvalidOperationException($"{source.Name}: 전체 재동기화에 실패했습니다.");
        }
        return imported;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _http.Dispose();
        // The semaphore and cancellation source are intentionally not disposed
        // here: an in-flight SynchronizeAsync may still execute its finally block.
    }

    private void SaveToken(GoogleToken verifiedToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        // Never truncate the only usable refresh token during a crash or a
        // disk-full error. Only VERIFIED credentials reach this checkpoint.
        var temp = _tokenPath + ".tmp";
        File.WriteAllBytes(temp, Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(verifiedToken))));
        if (File.Exists(_tokenPath)) File.Replace(temp, _tokenPath, null, ignoreMetadataErrors: true);
        else File.Move(temp, _tokenPath);
    }

    private static string FormValues(IReadOnlyDictionary<string, string> values)
        => string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr ptr);

    private static byte[] Protect(byte[] input) => DataProtection(input, true);
    private static byte[] Unprotect(byte[] input) => DataProtection(input, false);

    private static byte[] DataProtection(byte[] raw, bool protect)
    {
        var buffer = Marshal.AllocHGlobal(raw.Length);
        try
        {
            Marshal.Copy(raw, 0, buffer, raw.Length);
            var data = new DataBlob { Length = raw.Length, Data = buffer };
            var result = default(DataBlob);
            var ok = protect
                ? CryptProtectData(ref data, "DesktopDeck Google Calendar", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result)
                : CryptUnprotectData(ref data, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var bytes = new byte[result.Length];
                Marshal.Copy(result.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { LocalFree(result.Data); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
