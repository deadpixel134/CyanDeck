using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using DesktopDeck.Services;

namespace DesktopDeck.Calendar;

// Built-in read-only ICS subscriptions, deliberately separate from the Google
// CalendarList and from the persisted, writable calendar-events.json store.
public sealed record KoreanHolidayCalendar(string Id, string Name, string Url, string Color, string CacheName);

public static class KoreanHolidayCalendars
{
    public const string AppleId = "builtin:holidays:apple-kr";
    public const string GoogleId = "builtin:holidays:google-kr";
    // Alternative Apple holiday subscription URL supplied by the user.
    // Use only if the primary Apple ICS feed returns invalid data or fails.
    public const string AppleMirrorUrl =
        "https://calendar.google.com/calendar/ical/qansohiecib58ga9k1bmppvt5oi65b1q%40import.calendar.google.com/public/basic.ics";
    // Keep the Google ICS URL supplied by the user as the primary source.
    // First retry the same calendar ID through Google's historical ICS host;
    // only then try the older Korean holiday ID published by Google.
    public const string GoogleSameIdMirrorUrl =
        "https://www.google.com/calendar/ical/ko.south_korea.official%23holiday%40group.v.calendar.google.com/public/basic.ics";
    public const string GoogleAlternateCalendarUrl =
        "https://calendar.google.com/calendar/ical/ko.south_korea%23holiday%40group.v.calendar.google.com/public/basic.ics";
    public static readonly IReadOnlyList<KoreanHolidayCalendar> All =
    [
        new(AppleId, "대한민국 공휴일 (애플 제공)",
            "https://p03-calendars.icloud.com/holidays/kr_ko.ics", "#F83A22", "holidays-apple-kr.ics"),
        new(GoogleId, "대한민국 공휴일 (구글 제공)",
            "https://calendar.google.com/calendar/ical/ko.south_korea.official%23holiday%40group.v.calendar.google.com/public/basic.ics",
            "#F83A22", "holidays-google-kr.ics")
    ];
    public static bool Contains(string? id) => id is AppleId or GoogleId;
}

// Limited importer for public holiday feeds: unfolding, escaped text, exclusive
// all-day DTEND, RDATE/EXDATE and standard yearly recurrence. Unsupported
// recurrence patterns are not fabricated into incorrect holiday dates.
public static class KoreanHolidayIcsParser
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const int MaximumEvents = 8000;

    public static IReadOnlyList<CalendarEvent> Parse(string ics, string sourceId, DateTime? today = null)
    {
        if (!KoreanHolidayCalendars.Contains(sourceId)) throw new ArgumentException("Unknown holiday feed", nameof(sourceId));
        ics = ics.TrimStart('\uFEFF', ' ', '\r', '\n');
        if (ics.Length > 4_000_000
            || !ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)
            || !ics.Contains("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This is not a valid bounded ICS calendar.");
        var lines = new List<string>();
        foreach (var line in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (lines.Count > 0) lines[^1] += line[1..];
            }
            else lines.Add(line);
        }
        var events = new List<CalendarEvent>();
        var active = false;
        var properties = new Dictionary<string, List<(string rawName, string value)>>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                active = true;
                properties.Clear();
                if (++count > MaximumEvents) throw new InvalidDataException("Too many ICS events.");
                continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (active) ParseEvent(properties, sourceId, today ?? DateTime.Today, events);
                active = false;
                continue;
            }
            if (!active) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Split(';')[0].ToUpperInvariant();
            if (!properties.TryGetValue(key, out var values)) properties[key] = values = [];
            values.Add((line[..colon], line[(colon + 1)..]));
        }
        // An empty but syntactically valid calendar is acceptable; never sync
        // its contents through the Google API or local editable event store.
        return events.GroupBy(e => (e.CalendarId, e.RemoteId, e.Date))
            .Select(group => group.First()).ToArray();
    }

    private static void ParseEvent(Dictionary<string, List<(string rawName, string value)>> p,
        string id, DateTime today, List<CalendarEvent> output)
    {
        static string? First(Dictionary<string, List<(string rawName, string value)>> map, string key)
            => map.TryGetValue(key, out var v) ? v.FirstOrDefault().value : null;
        if (string.Equals(First(p, "STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase)) return;
        var startRaw = First(p, "DTSTART");
        var titleRaw = First(p, "SUMMARY");
        if (startRaw is null || titleRaw is null || !TryDate(startRaw, out var date)) return;
        var title = Unescape(titleRaw).Trim();
        if (title.Length == 0) return;
        var allDay = p.TryGetValue("DTSTART", out var startFields) &&
            (startFields[0].rawName.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase)
              || startRaw.Length == 8);
        // Most public holiday calendars contain date-only VEVENTs. For timed
        // events, represent the date without inventing a local time-zone offset.
        var end = date;
        if (TryDate(First(p, "DTEND"), out var endDate))
            end = (allDay ? endDate.AddDays(-1) : endDate).Date;
        if (end < date) end = date;
        var durationDays = Math.Clamp((end - date).Days, 0, 30);
        var uid = First(p, "UID") ?? (date.ToString("yyyyMMdd", Invariant) + ":" + title);
        var exceptions = new HashSet<DateTime>();
        if (p.TryGetValue("EXDATE", out var excluded))
            foreach (var field in excluded)
                foreach (var item in field.value.Split(','))
                    if (TryDate(item, out var d)) exceptions.Add(d);
        var instances = new HashSet<DateTime> { date };
        if (p.TryGetValue("RDATE", out var added))
            foreach (var field in added)
                foreach (var item in field.value.Split(','))
                    if (TryDate(item.Split('/')[0], out var d)) instances.Add(d);
        var rule = First(p, "RRULE");
        if (rule is not null)
            foreach (var instance in ExpandYearly(rule, date, today)) instances.Add(instance);
        foreach (var instance in instances.Except(exceptions))
        {
            if (output.Count >= MaximumEvents * 15) break;
            output.Add(new CalendarEvent
            {
                Id = DeterministicGuid(id + "|" + uid + "|" + instance.ToString("yyyyMMdd", Invariant)),
                Date = instance,
                EndDate = instance.AddDays(durationDays),
                CalendarId = id,
                Title = title,
                RemoteId = "ics:" + uid + ":" + instance.ToString("yyyyMMdd", Invariant),
                RemoteReadOnly = true,
                Dirty = false
            });
        }
    }

    private static IEnumerable<DateTime> ExpandYearly(string rule, DateTime first, DateTime today)
    {
        var parts = rule.Split(';').Select(v => v.Split('=', 2))
            .Where(v => v.Length == 2).ToDictionary(v => v[0].ToUpperInvariant(), v => v[1], StringComparer.OrdinalIgnoreCase);
        if (!parts.TryGetValue("FREQ", out var freq) || freq != "YEARLY") yield break;
        var interval = parts.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var parsed) ? parsed : 1;
        if (interval is < 1 or > 100) yield break;
        var months = NumericParts(parts, "BYMONTH", first.Month, 1, 12);
        var days = NumericParts(parts, "BYMONTHDAY", first.Day, -31, 31);
        // Avoid manufacturing dates from complex recurrence syntax not handled here.
        if (parts.ContainsKey("BYSETPOS") || parts.ContainsKey("BYYEARDAY") || parts.ContainsKey("BYWEEKNO")
            || (parts.TryGetValue("BYDAY", out var weekdayValue) &&
                weekdayValue.Split(',').Any(v => v.Length != 2))) yield break;
        var until = parts.TryGetValue("UNTIL", out var untilText) && TryDate(untilText, out var end) ? end : DateTime.MaxValue;
        int maxCount = int.MaxValue;
        var hasCount = parts.TryGetValue("COUNT", out var countText) && int.TryParse(countText, out maxCount);
        var generated = 0;
        var firstYear = Math.Max(first.Year, today.Year - 8);
        var lastYear = Math.Min(2100, today.Year + 12);
        // COUNT is measured from the original recurrence start, not the display window.
        if (hasCount) firstYear = first.Year;
        for (var year = firstYear; year <= lastYear; year++)
        {
            if ((year - first.Year) % interval != 0) continue;
            foreach (var month in months)
            {
                foreach (var day in days)
                {
                    if (day == 0) continue;
                    var resolved = day > 0 ? day : DateTime.DaysInMonth(year, month) + day + 1;
                    if (resolved < 1 || resolved > DateTime.DaysInMonth(year, month)) continue;
                    var instance = new DateTime(year, month, resolved);
                    if (instance < first || instance > until) continue;
                    if (parts.TryGetValue("BYDAY", out var byday) &&
                        !byday.Split(',').Any(d => MatchesWeekday(instance.DayOfWeek, d))) continue;
                    generated++;
                    if (hasCount && generated > maxCount) yield break;
                    yield return instance;
                }
            }
        }
    }

    private static bool MatchesWeekday(DayOfWeek day, string value)
    {
        // Bare BYDAY weekdays only; ordinal BYDAY is deliberately not guessed.
        var weekday = day switch
        {
            DayOfWeek.Sunday => "SU", DayOfWeek.Monday => "MO", DayOfWeek.Tuesday => "TU",
            DayOfWeek.Wednesday => "WE", DayOfWeek.Thursday => "TH", DayOfWeek.Friday => "FR", _ => "SA"
        };
        return string.Equals(value, weekday, StringComparison.Ordinal);
    }

    private static IEnumerable<int> NumericParts(Dictionary<string, string> p, string key, int fallback, int min, int max)
    {
        if (!p.TryGetValue(key, out var text)) return [fallback];
        return text.Split(',').Select(x => int.TryParse(x, out var n) ? n : 0)
            .Where(x => x >= min && x <= max && x != 0).Distinct().ToArray();
    }

    private static bool TryDate(string? raw, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8) return false;
        return DateTime.TryParseExact(raw[..8], "yyyyMMdd", Invariant, DateTimeStyles.None, out date);
    }

    private static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static Guid DeterministicGuid(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(hash[..16]);
    }
}

public sealed record HolidayFeedRefreshResult(
    string CalendarId, bool Success, int EventCount, bool UsedFallback, string Error = "");

public sealed class KoreanHolidayFeedService : IDisposable
{
    private const int MaxIcsBytes = 4_000_000;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(18);
    private readonly string _cacheDirectory;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Dictionary<string, IReadOnlyList<CalendarEvent>> _cached = new(StringComparer.Ordinal);
    private IReadOnlyList<HolidayFeedRefreshResult> _lastResults = [];
    public IReadOnlyList<CalendarEvent> Events => _cached.Values.SelectMany(x => x).ToArray();
    public IReadOnlyList<HolidayFeedRefreshResult> LastResults => _lastResults;

    // Optional injected client is used by the non-network domain regression harness.
    public KoreanHolidayFeedService(string? cacheDirectory = null, HttpClient? client = null)
    {
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = RequestDeadline };
        _ownsClient = client is null;
        if (_ownsClient) _client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopDeck/7.49.3");
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopDeck", "holiday-feeds");
        foreach (var source in KoreanHolidayCalendars.All) LoadCache(source);
    }

    public async Task<IReadOnlyList<HolidayFeedRefreshResult>> RefreshAsync(
        IEnumerable<string> sourceIds, CancellationToken cancellationToken)
    {
        var selected = new HashSet<string>(sourceIds, StringComparer.Ordinal);
        var results = new List<HolidayFeedRefreshResult>();
        foreach (var source in KoreanHolidayCalendars.All.Where(x => selected.Contains(x.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var errors = new List<string>();
            var urls = source.Id switch
            {
                KoreanHolidayCalendars.AppleId => new[] { source.Url, KoreanHolidayCalendars.AppleMirrorUrl },
                KoreanHolidayCalendars.GoogleId => new[] { source.Url,
                    KoreanHolidayCalendars.GoogleSameIdMirrorUrl, KoreanHolidayCalendars.GoogleAlternateCalendarUrl },
                _ => new[] { source.Url }
            };
            var success = false;
            for (var i = 0; i < urls.Length; i++)
            {
                try
                {
                    var text = await DownloadIcsAsync(urls[i], cancellationToken);
                    var parsed = KoreanHolidayIcsParser.Parse(text, source.Id);
                    if (parsed.Count == 0)
                        throw new InvalidDataException("ICS calendar has no readable holiday events");
                    // Publish only a fully validated subscription. A bad HTTP response
                    // must never erase a previously working cache or Google/local events.
                    _cached[source.Id] = parsed;
                    SaveCache(source, text);
                    results.Add(new HolidayFeedRefreshResult(source.Id, true, parsed.Count, i != 0));
                    success = true;
                    if (i != 0)
                        DiagnosticsLog.Warn("Holiday ICS primary failed; alternative endpoint loaded for " + source.Name);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Some Apple and Google public endpoints return HTML or errors.
                    // Try alternative public ICS URLs; never import HTML as events.
                    var reason = ex is OperationCanceledException ? "시간 초과" : ex.Message;
                    errors.Add(reason);
                    DiagnosticsLog.Warn("Holiday ICS update failed for " + source.Name
                        + " (endpoint " + (i + 1) + "/" + urls.Length + "): " + reason);
                }
            }
            if (!success)
                results.Add(new HolidayFeedRefreshResult(source.Id, false,
                    _cached.TryGetValue(source.Id, out var previous) ? previous.Count : 0,
                    false, string.Join(" / ", errors)));
        }
        _lastResults = results.ToArray();
        return _lastResults;
    }

    // Headers-only HttpClient timeouts do not cover streaming the response body.
    // Link an 18-second deadline with user cancellation and cap bytes while reading.
    private async Task<string> DownloadIcsAsync(string url, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxIcsBytes)
            throw new InvalidDataException("ICS feed exceeds size limit");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var memory = new MemoryStream();
        var block = new byte[16_384];
        while (true)
        {
            var count = await stream.ReadAsync(block.AsMemory(), deadline.Token);
            if (count == 0) break;
            if (memory.Length + count > MaxIcsBytes)
                throw new InvalidDataException("ICS feed exceeds size limit");
            memory.Write(block, 0, count);
        }
        memory.Position = 0;
        using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(deadline.Token);
    }

    public static string DescribeResults(IReadOnlyList<HolidayFeedRefreshResult> results)
    {
        if (results.Count == 0) return "선택한 기본 제공 공휴일 캘린더가 없습니다. 체크박스에서 먼저 활성화하세요.";
        return string.Join(" | ", results.Select(result =>
        {
            var name = result.CalendarId == KoreanHolidayCalendars.AppleId ? "애플 공휴일" : "구글 공휴일";
            if (result.Success)
                return $"{name}: {result.EventCount}개 갱신 완료" + (result.UsedFallback ? " (대체 주소 사용)" : "");
            var detail = string.IsNullOrWhiteSpace(result.Error) ? "알 수 없는 오류" : result.Error;
            return $"{name}: 가져오기 실패 ({detail})" +
                (result.EventCount > 0 ? $" · 기존 {result.EventCount}개 캐시 유지" : " · 저장된 데이터 없음");
        }));
    }

    private void SaveCache(KoreanHolidayCalendar source, string text)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = Path.Combine(_cacheDirectory, source.CacheName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException ex) { DiagnosticsLog.Warn("Holiday ICS cache save failed: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { DiagnosticsLog.Warn("Holiday ICS cache save failed: " + ex.Message); }
    }

    private void LoadCache(KoreanHolidayCalendar source)
    {
        try
        {
            var path = Path.Combine(_cacheDirectory, source.CacheName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxIcsBytes) return;
            _cached[source.Id] = KoreanHolidayIcsParser.Parse(File.ReadAllText(path, Encoding.UTF8), source.Id);
        }
        catch (Exception ex) { DiagnosticsLog.Warn("Holiday ICS cache read failed: " + ex.Message); }
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
