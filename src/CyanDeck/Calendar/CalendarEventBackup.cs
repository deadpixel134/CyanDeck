using System.Globalization;
using System.Text.Json;

namespace CyanDeck.Calendar;

// Portable copy of the events that exist ONLY on this PC (local-only events and
// events never uploaded to Google), plus their CyanDeck-only colors. Google-linked
// events are not included: Google keeps them, and importing them as local copies
// would duplicate them after the next sync.
public sealed class CalendarEventBackupFile
{
    // No initializers: a file without these values is not a calendar backup.
    public string Format { get; set; } = "";
    public int Version { get; set; }
    public DateTime ExportedUtc { get; set; }
    public List<CalendarEvent> Events { get; set; } = [];
    public Dictionary<string, CalendarEventAppearance> Appearance { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CalendarEventImportPlan
{
    public List<CalendarEvent> Added { get; } = [];
    public Dictionary<Guid, CalendarEventAppearance> Appearance { get; } = new();
    public int SkippedDuplicates { get; set; }
    public int SkippedLinked { get; set; }
    public int SkippedInvalid { get; set; }
}

public static class CalendarEventBackup
{
    public const string FormatName = "CyanDeck.CalendarEvents";
    public const int CurrentVersion = 1;
    public const int MaxEvents = 100000;
    public const string CompanionSuffix = ".calendar-events.json";
    private const long MaxFileBytes = 64L * 1024 * 1024;

    // A create whose upload was never confirmed (RemoteCreatePending) still exists only
    // here; the exported copy carries no remote fields, so it imports as a local event.
    public static bool IsLocalEvent(CalendarEvent ev)
        => !ev.Deleted && (ev.RemoteId is null || ev.RemoteCreatePending);

    public static int CountLocalEvents(IEnumerable<CalendarEvent> events) => events.Count(IsLocalEvent);

    // "CyanDeck-backup-20261027-1200.json" -> "CyanDeck-backup-20261027-1200.calendar-events.json"
    public static string CompanionPathFor(string configurationPath)
    {
        var full = Path.GetFullPath(configurationPath);
        var folder = Path.GetDirectoryName(full) ?? throw new IOException("내보낸 파일의 폴더를 확인할 수 없습니다.");
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(full) + CompanionSuffix);
    }

    public static int Export(IEnumerable<CalendarEvent> events, Func<Guid, CalendarEventAppearance> appearance, string path)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(appearance);
        var file = new CalendarEventBackupFile
        {
            Format = FormatName, Version = CurrentVersion, ExportedUtc = DateTime.UtcNow
        };
        foreach (var ev in events.Where(IsLocalEvent))
        {
            file.Events.Add(new CalendarEvent
            {
                Id = ev.Id, Date = ev.Date, EndDate = ev.EndDate, StartAt = ev.StartAt, EndAt = ev.EndAt,
                Title = ev.Title, CalendarId = ev.CalendarId, LocalOnly = ev.LocalOnly, Dirty = ev.Dirty
            });
            var style = appearance(ev.Id);
            if (style is not null && (style.BackgroundColor is not null || style.TextColor is not null))
                file.Appearance[ev.Id.ToString("N")] = style;
        }
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? throw new IOException("저장할 폴더를 확인할 수 없습니다."));
        var temp = full + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, full, overwrite: true);
        return file.Events.Count;
    }

    // Companion file written next to a layout/settings export. Nothing is written
    // when there is no local event to protect, except that an older companion with the
    // same name is overwritten by an empty backup so it cannot be paired with this
    // export and later restore events the user has since deleted.
    public static int ExportCompanion(IEnumerable<CalendarEvent> events, string configurationPath)
    {
        var local = events.Where(IsLocalEvent).ToList();
        var companion = CompanionPathFor(configurationPath);
        if (local.Count == 0)
        {
            if (File.Exists(companion)) Export(local, _ => new CalendarEventAppearance(), companion);
            return 0;
        }
        var appearance = new CalendarEventAppearanceStore();
        return Export(local, appearance.Get, companion);
    }

    public static CalendarEventBackupFile Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("일정 백업 파일을 찾을 수 없습니다.", path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("일정 백업 파일이 너무 큽니다.");
        CalendarEventBackupFile? file;
        try { file = JsonSerializer.Deserialize<CalendarEventBackupFile>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException("CyanDeck 일정 백업 파일이 아닙니다.", ex); }
        if (file is null || !string.Equals(file.Format, FormatName, StringComparison.Ordinal))
            throw new InvalidDataException("CyanDeck 일정 백업 파일이 아닙니다.");
        if (file.Version < 1 || file.Version > CurrentVersion)
            throw new InvalidDataException("이 버전의 CyanDeck에서 읽을 수 없는 일정 백업입니다.");
        file.Events ??= [];
        file.Appearance ??= new(StringComparer.Ordinal);
        if (file.Events.Count > MaxEvents || file.Events.Any(e => e is null))
            throw new InvalidDataException("일정 백업 파일의 일정 목록이 올바르지 않습니다.");
        return file;
    }

    // Imported events always become local-only events: they are never uploaded to
    // the current Google account and never replace an existing event.
    public static CalendarEventImportPlan PlanImport(IReadOnlyList<CalendarEvent> existing, CalendarEventBackupFile backup)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(backup);
        var plan = new CalendarEventImportPlan();
        var ids = new HashSet<Guid>(existing.Select(e => e.Id));
        var signatures = new HashSet<string>(existing.Where(e => !e.Deleted).Select(Signature), StringComparer.Ordinal);
        foreach (var source in backup.Events)
        {
            if (source.Deleted) continue;
            if (source.RemoteId is not null) { plan.SkippedLinked++; continue; }
            if (string.IsNullOrWhiteSpace(source.Title) || source.StartAt.HasValue != source.EndAt.HasValue
                || (source.StartAt.HasValue && source.EndAt <= source.StartAt))
            {
                plan.SkippedInvalid++;
                continue;
            }
            if (!signatures.Add(Signature(source))) { plan.SkippedDuplicates++; continue; }
            var id = source.Id == Guid.Empty || ids.Contains(source.Id) ? Guid.NewGuid() : source.Id;
            ids.Add(id);
            var start = source.Date.Date;
            var end = source.EndDate?.Date ?? start;
            if (end < start) end = start;
            plan.Added.Add(new CalendarEvent
            {
                Id = id, Date = start, EndDate = end, StartAt = source.StartAt, EndAt = source.EndAt,
                Title = source.Title.Trim(), CalendarId = CalendarEventStore.LocalCalendarId,
                LocalOnly = true, Dirty = false
            });
            if (backup.Appearance.TryGetValue(source.Id.ToString("N"), out var style) && style is not null
                && (style.BackgroundColor is not null || style.TextColor is not null)
                && CalendarEventAppearanceStore.IsValid(style.BackgroundColor)
                && CalendarEventAppearanceStore.IsValid(style.TextColor))
                plan.Appearance[id] = style;
        }
        return plan;
    }

    private static string Signature(CalendarEvent ev)
        => string.Join("\u001F",
            (ev.Title ?? "").Trim(),
            ev.Date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ev.EffectiveEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ev.StartAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "",
            ev.EndAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "");
}
