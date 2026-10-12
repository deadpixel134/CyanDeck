using System.Reflection;
using System.Text.Json;
using DesktopDeck.Models;

namespace DesktopDeck.Services;

public static class SettingsAuditService
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        nameof(DeckSettings.LastUpdateCheckUtc),
        nameof(DeckSettings.PeekHotkeyLabel),
        nameof(DeckSettings.DesktopDisplayHotkeyLabel),
        nameof(DeckSettings.QuickHideHotkeyLabel)
    };

    public static string AuditPath => Path.Combine(DiagnosticsLog.LogFolder, "settings-audit.jsonl");

    public static IReadOnlyDictionary<string, string> Capture(DeckSettings settings)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in typeof(DeckSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || Ignored.Contains(property.Name)) continue;
            result[property.Name] = Sanitize(property.Name, property.GetValue(settings));
        }
        return result;
    }

    public static int RecordChanges(IReadOnlyDictionary<string, string> before, DeckSettings after, string source)
    {
        try
        {
            var current = Capture(after);
            var changes = new List<object>();
            foreach (var pair in current)
            {
                before.TryGetValue(pair.Key, out var oldValue);
                if (string.Equals(oldValue, pair.Value, StringComparison.Ordinal)) continue;
                changes.Add(new { setting = pair.Key, oldValue = oldValue ?? "<missing>", newValue = pair.Value });
            }
            if (changes.Count == 0) return 0;

            Directory.CreateDirectory(DiagnosticsLog.LogFolder);
            var record = new
            {
                timestampUtc = DateTime.UtcNow,
                source,
                processId = Environment.ProcessId,
                changes
            };
            var line = JsonSerializer.Serialize(record);
            lock (Gate) File.AppendAllText(AuditPath, line + Environment.NewLine);
            DiagnosticsLog.Info($"Settings audit recorded {changes.Count} change(s) from {source}.");
            return changes.Count;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to write settings audit log", ex);
            return 0;
        }
    }

    private static string Sanitize(string propertyName, object? value)
    {
        if (propertyName is nameof(DeckSettings.UpdateManifestSource) or nameof(DeckSettings.UpdatePublicKeyPath))
            return string.IsNullOrWhiteSpace(value?.ToString()) ? "<empty>" : "<configured>";
        return value switch
        {
            null => "<null>",
            bool b => b ? "true" : "false",
            DateTime dt => dt.ToUniversalTime().ToString("O"),
            _ => value.ToString() ?? "<null>"
        };
    }
}
