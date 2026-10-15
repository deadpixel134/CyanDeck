using System.Text.Json;
using System.Text.RegularExpressions;
using CyanDeck.Services;

namespace CyanDeck.Calendar;

public sealed class GoogleCalendarSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#5D91DD";
    public string AccessRole { get; set; } = "reader";
    public bool IsPrimary { get; set; }
    // The Google CalendarList selected/hidden flags seed first connection only.
    // After that, CyanDeck's visible-calendar selection is independent.
    public bool SelectedInGoogle { get; set; }
    public bool HiddenInGoogle { get; set; }
    // writerWithoutPrivateAccess can create/edit events, but cannot read private details.
    public bool CanWrite => AccessRole is "owner" or "writer" or "writerWithoutPrivateAccess";
    public override string ToString() => Name + (CanWrite ? "" : " (읽기 전용)");
}

public sealed class CalendarPreferences
{
    // Null is the first-run sentinel; empty is a deliberate choice to show none.
    public List<string>? SelectedCalendarIds { get; set; }
    public string? TargetCalendarId { get; set; }
    // New-event-only default; old users retain their Google target behavior.
    public bool DefaultNewEventsLocalOnly { get; set; }
    public Dictionary<string, string> ColorOverrides { get; set; } = new(StringComparer.Ordinal);
    public List<GoogleCalendarSource> Catalog { get; set; } = [];
    // Saved with the catalog in one checkpoint. Old preferences will refresh once.
    public DateTime CalendarCatalogRefreshedUtc { get; set; } = DateTime.MinValue;
    public Dictionary<string, string> SyncTokens { get; set; } = new(StringComparer.Ordinal);
    // Stored in the existing calendar-preferences.json (no StateSchema v35 migration).
    public int AutoSyncIntervalMinutes { get; set; } = CalendarSyncIntervalPolicy.DefaultMinutes;
    // Independent, read-only public ICS feeds; never part of the Google account catalog.
    // Initializers also apply when loading older v26 preferences without these keys.
    public bool ShowAppleKoreanHolidays { get; set; } = true;
    public bool ShowGoogleKoreanHolidays { get; set; } = false;
}

public sealed class CalendarPreferencesStore
{
    private readonly string _path;
    public CalendarPreferences Data { get; private set; } = new();
    public bool IsReadOnly { get; private set; }
    public bool IsRecoveredFromBackup { get; private set; }

    public CalendarPreferencesStore(string? storagePath = null)
    {
        _path = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck", "calendar-preferences.json");
        try
        {
            if (File.Exists(_path)) Data = ReadPreferences(_path);
            else if (File.Exists(_path + ".bak"))
            {
                Data = ReadPreferences(_path + ".bak");
                IsReadOnly = true;
                IsRecoveredFromBackup = true;
                DiagnosticsLog.Warn("Calendar preferences primary missing; showing backup read-only");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Cannot read calendar preferences; refusing overwrite", ex);
            IsReadOnly = true;
            try
            {
                if (File.Exists(_path + ".bak"))
                {
                    Data = ReadPreferences(_path + ".bak");
                    IsRecoveredFromBackup = true;
                    DiagnosticsLog.Warn("Calendar preferences backup loaded read-only; damaged file preserved");
                }
            }
            catch (Exception backupError)
            {
                DiagnosticsLog.Error("Calendar preferences backup could not be read either", backupError);
            }
        }
    }

    // Confirmed recovery only. Google sync tokens are invalidated by the
    // caller BEFORE recovering older event snapshots, to force a full sync.
    public string? RestoreFromBackup()
    {
        if (!IsRecoveredFromBackup || !IsReadOnly)
            throw new InvalidOperationException("복구 가능한 읽기 전용 설정 백업이 없습니다.");
        var (restored, archived) = CalendarBackupRestorer.Restore(_path, ReadPreferences);
        Data = restored;
        IsRecoveredFromBackup = false;
        IsReadOnly = false;
        return archived;
    }

    public void InvalidateAllSyncTokens()
    {
        CommitUpdate(next => next.SyncTokens.Clear());
    }

    private static CalendarPreferences ReadPreferences(string path)
    {
        var result = JsonSerializer.Deserialize<CalendarPreferences>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Calendar preferences JSON is null.");
        result.Catalog ??= [];
        result.ColorOverrides ??= new(StringComparer.Ordinal);
        result.SyncTokens ??= new(StringComparer.Ordinal);
        // Older preference files omit this property and use the initializer (5 minutes).
        result.AutoSyncIntervalMinutes = CalendarSyncIntervalPolicy.Normalize(result.AutoSyncIntervalMinutes);
        return result;
    }

    public void UpdateCatalog(IReadOnlyList<GoogleCalendarSource> sources)
    {
        var next = Clone(Data);
        next.Catalog = sources.ToList();
        next.CalendarCatalogRefreshedUtc = DateTime.UtcNow;
        if (next.SelectedCalendarIds is null)
        {
            // Respect the user's existing Google Calendar selected set on first
            // connect. Never reset a later explicit 'none visible' choice.
            var initial = sources.Where(x => x.SelectedInGoogle && !x.HiddenInGoogle)
                .Select(x => x.Id).Distinct(StringComparer.Ordinal).ToList();
            if (initial.Count == 0)
            {
                var primary = sources.FirstOrDefault(x => x.IsPrimary) ?? sources.FirstOrDefault();
                if (primary is not null) initial.Add(primary.Id);
            }
            next.SelectedCalendarIds = initial;
        }
        if (string.IsNullOrEmpty(next.TargetCalendarId)
            || !sources.Any(x => x.Id == next.TargetCalendarId && x.CanWrite))
            next.TargetCalendarId = sources.FirstOrDefault(x => x.IsPrimary && x.CanWrite)?.Id
                ?? sources.FirstOrDefault(x => x.CanWrite)?.Id;
        SaveSnapshot(next);
        Data = next;
    }

    // A consumed sync token must never exist only in memory. Update it as
    // one atomic preferences checkpoint before publishing the new state.
    public void UpdateSyncToken(string calendarId, string? token)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("Missing calendar ID", nameof(calendarId));
        var next = Clone(Data);
        if (string.IsNullOrEmpty(token)) next.SyncTokens.Remove(calendarId);
        else next.SyncTokens[calendarId] = token;
        SaveSnapshot(next);
        Data = next;
    }

    // UI preferences use the same durable-then-publish commit boundary as
    // remote catalog and sync-token updates. A failed write cannot change the
    // live selection, target calendar, or color overrides.
    public void CommitUpdate(Action<CalendarPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var next = Clone(Data);
        update(next);
        SaveSnapshot(next);
        Data = next;
    }

    public void UpdateAutoSyncInterval(int minutes)
    {
        if (!CalendarSyncIntervalPolicy.IsValid(minutes))
            throw new ArgumentOutOfRangeException(nameof(minutes), "Interval must be 0~1440 minutes.");
        CommitUpdate(next => next.AutoSyncIntervalMinutes = minutes);
    }

    public void UpdateTargetCalendar(string? calendarId)
    {
        CommitUpdate(next => next.TargetCalendarId = calendarId);
    }

    public void SetHolidayVisibility(string calendarId, bool visible)
    {
        if (!KoreanHolidayCalendars.Contains(calendarId))
            throw new ArgumentException("Unknown built-in calendar", nameof(calendarId));
        CommitUpdate(next =>
        {
            if (calendarId == KoreanHolidayCalendars.AppleId) next.ShowAppleKoreanHolidays = visible;
            else next.ShowGoogleKoreanHolidays = visible;
        });
    }

    public IReadOnlyList<string> VisibleHolidayIds()
    {
        var visible = new List<string>();
        if (Data.ShowAppleKoreanHolidays) visible.Add(KoreanHolidayCalendars.AppleId);
        if (Data.ShowGoogleKoreanHolidays) visible.Add(KoreanHolidayCalendars.GoogleId);
        return visible;
    }

    public void SetCalendarVisibility(string calendarId, bool visible)
    {
        if (string.IsNullOrWhiteSpace(calendarId))
            throw new ArgumentException("Missing calendar ID", nameof(calendarId));
        CommitUpdate(next =>
        {
            var selected = next.SelectedCalendarIds ??= [];
            if (visible && !selected.Contains(calendarId, StringComparer.Ordinal)) selected.Add(calendarId);
            if (!visible) selected.RemoveAll(id => string.Equals(id, calendarId, StringComparison.Ordinal));
        });
    }

    // Bulk visible-calendar changes are atomic: one durable checkpoint.
    // They affect only CyanDeck, never Google's own CalendarList selections.
    public void SetVisibleCalendars(IEnumerable<string> calendarIds)
    {
        ArgumentNullException.ThrowIfNull(calendarIds);
        var chosen = calendarIds.Distinct(StringComparer.Ordinal).ToList();
        if (chosen.Any(id => string.IsNullOrWhiteSpace(id)
            || !Data.Catalog.Any(source => string.Equals(source.Id, id, StringComparison.Ordinal))))
            throw new ArgumentException("선택한 캘린더가 현재 연결된 Google 계정 목록에 없습니다.", nameof(calendarIds));
        CommitUpdate(next => next.SelectedCalendarIds = chosen);
    }

    public void SetColorOverride(string calendarId, string? color)
    {
        if (string.IsNullOrWhiteSpace(calendarId))
            throw new ArgumentException("Missing calendar ID", nameof(calendarId));
        if (color is not null && !Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            throw new ArgumentException("Color must have the #RRGGBB form", nameof(color));
        CommitUpdate(next =>
        {
            if (color is null) next.ColorOverrides.Remove(calendarId);
            else next.ColorOverrides[calendarId] = color.ToUpperInvariant();
        });
    }

    private static CalendarPreferences Clone(CalendarPreferences data)
        => JsonSerializer.Deserialize<CalendarPreferences>(JsonSerializer.Serialize(data)) ?? new CalendarPreferences();

    public bool IsVisible(string? id)
    {
        if (id == KoreanHolidayCalendars.AppleId) return Data.ShowAppleKoreanHolidays;
        if (id == KoreanHolidayCalendars.GoogleId) return Data.ShowGoogleKoreanHolidays;
        if (Data.SelectedCalendarIds is null) return id is null or "primary";
        if (id is null or "primary")
            return Data.SelectedCalendarIds.Contains("primary") || Data.Catalog.Any(x => x.IsPrimary && Data.SelectedCalendarIds.Contains(x.Id));
        return Data.SelectedCalendarIds.Contains(id);
    }

    public string ColorFor(string? calendarId) => ColorFor(Data, calendarId);

    // Legacy local events use "primary" while Google CalendarList returns the
    // primary calendar's actual ID. An override for that calendar must apply
    // consistently to both representations, without rewriting older events.
    internal static string ColorFor(CalendarPreferences data, string? calendarId)
    {
        var holiday = KoreanHolidayCalendars.All.FirstOrDefault(source => source.Id == calendarId);
        if (holiday is not null)
            return data.ColorOverrides.TryGetValue(holiday.Id, out var holidayColor)
                && Regex.IsMatch(holidayColor, "^#[0-9A-Fa-f]{6}$")
                ? holidayColor : holiday.Color;
        var actualId = calendarId is null or "primary"
            ? data.Catalog.FirstOrDefault(c => c.IsPrimary)?.Id ?? calendarId
            : calendarId;
        if (actualId is not null && data.ColorOverrides.TryGetValue(actualId, out var value)) return value;
        return data.Catalog.FirstOrDefault(c => c.Id == actualId)?.Color ?? "#5D91DD";
    }

    public void Save() => SaveSnapshot(Data);

    private void SaveSnapshot(CalendarPreferences snapshot)
    {
        if (IsReadOnly) throw new IOException("Calendar preferences are in safe read-only mode.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
        else File.Move(temp, _path);
    }
}
