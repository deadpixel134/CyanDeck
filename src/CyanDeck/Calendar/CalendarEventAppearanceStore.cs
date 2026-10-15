using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CyanDeck.Services;

namespace CyanDeck.Calendar;

/// <summary>
/// Per-event CyanDeck-only visual preferences. Never mutates Google events,
/// calendar sync metadata or the primary calendar-events.json.
/// Holiday event IDs are deterministic, so preferences survive feed refreshes.
/// </summary>
public sealed record CalendarEventAppearance(string? BackgroundColor = null, string? TextColor = null);

public sealed class CalendarEventAppearanceStore
{
    private readonly string _path;
    private Dictionary<string, CalendarEventAppearance> _styles = new(StringComparer.Ordinal);
    public bool IsReadOnly { get; private set; }
    public bool IsRecoveredFromBackup { get; private set; }

    public CalendarEventAppearanceStore(string? path = null)
    {
        _path = path ?? Path.Combine(DataRootPaths.ActiveRoaming, "calendar-event-appearance.json");
        try
        {
            if (File.Exists(_path)) _styles = Read(_path);
            else if (File.Exists(_path + ".bak"))
            {
                _styles = Read(_path + ".bak");
                IsReadOnly = true;
                IsRecoveredFromBackup = true;
                DiagnosticsLog.Warn("Event appearance backup loaded read-only; missing primary remains untouched");
            }
        }
        catch (Exception ex)
        {
            IsReadOnly = true;
            DiagnosticsLog.Error("Event appearance primary unavailable; file was not overwritten", ex);
            try
            {
                if (File.Exists(_path + ".bak"))
                {
                    _styles = Read(_path + ".bak");
                    IsRecoveredFromBackup = true;
                }
            }
            catch (Exception backupError)
            {
                DiagnosticsLog.Error("Event appearance backup unavailable", backupError);
            }
        }
    }

    private static Dictionary<string, CalendarEventAppearance> Read(string path)
    {
        var loaded = JsonSerializer.Deserialize<Dictionary<string, CalendarEventAppearance>>(
            File.ReadAllText(path)) ?? throw new InvalidDataException("Appearance settings are null.");
        if (loaded.Count > 100000 ||
            loaded.Any(item => !Guid.TryParseExact(item.Key, "N", out _) ||
                item.Value is null ||
                !IsValid(item.Value.BackgroundColor) || !IsValid(item.Value.TextColor)))
            throw new InvalidDataException("Appearance settings contain invalid event IDs or colors.");
        return new Dictionary<string, CalendarEventAppearance>(loaded, StringComparer.Ordinal);
    }

    public static bool IsValid(string? color) => color is null ||
        (color.Length == 7 && color[0] == '#' &&
         color.Skip(1).All(Uri.IsHexDigit));

    public CalendarEventAppearance Get(Guid eventId)
        => _styles.TryGetValue(eventId.ToString("N"), out var appearance)
            ? appearance : new CalendarEventAppearance();

    public void Set(Guid eventId, string? backgroundColor, string? textColor)
    {
        if (IsReadOnly) throw new IOException("일정 표시 설정이 읽기 전용입니다. 원본 파일을 확인하세요.");
        if (eventId == Guid.Empty || !IsValid(backgroundColor) || !IsValid(textColor))
            throw new ArgumentException("일정 색상은 #RRGGBB 형식이어야 합니다.");
        var snapshot = new Dictionary<string, CalendarEventAppearance>(_styles, StringComparer.Ordinal);
        var key = eventId.ToString("N");
        if (backgroundColor is null && textColor is null) snapshot.Remove(key);
        else snapshot[key] = new CalendarEventAppearance(
            backgroundColor?.ToUpperInvariant(), textColor?.ToUpperInvariant());
        var parent = Path.GetDirectoryName(_path)
            ?? throw new IOException("일정 표시 설정 저장 위치가 올바르지 않습니다.");
        Directory.CreateDirectory(parent);
        var temp = _path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
                File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temp, _path);
            _styles = snapshot;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Could not save event-local appearance; old settings retained", ex);
            throw;
        }
    }
}
