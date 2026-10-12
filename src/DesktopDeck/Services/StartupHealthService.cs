using System.Text.Json;

namespace DesktopDeck.Services;

public sealed record StartupHealthSnapshot(int RecentUncleanStarts, DateTime? LastUncleanUtc, bool ShouldUseSafeMode)
{
    public string Summary => RecentUncleanStarts == 0
        ? "최근 비정상 시작이 감지되지 않았습니다."
        : $"보호 판단 시간 내 비정상 시작 {RecentUncleanStarts}회" + (LastUncleanUtc is null ? "." : $"; 마지막 {LastUncleanUtc.Value.ToLocalTime():g}.");
}

public static class StartupHealthService
{
    private sealed class StartupHealthState
    {
        public List<DateTime> UncleanStartsUtc { get; set; } = [];
        public DateTime? LastCleanExitUtc { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopDeck");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "startup-health.json");

    public static StartupHealthSnapshot RecordUncleanStart(int windowMinutes, int threshold)
    {
        lock (Gate)
        {
            var state = Load();
            var now = DateTime.UtcNow;
            var cutoff = now.AddMinutes(-Math.Clamp(windowMinutes, 2, 120));
            state.UncleanStartsUtc = state.UncleanStartsUtc.Where(x => x >= cutoff && x <= now.AddMinutes(1)).ToList();
            state.UncleanStartsUtc.Add(now);
            Save(state);
            return Snapshot(state, cutoff, threshold);
        }
    }

    public static StartupHealthSnapshot GetSnapshot(int windowMinutes, int threshold)
    {
        lock (Gate)
        {
            var state = Load();
            var cutoff = DateTime.UtcNow.AddMinutes(-Math.Clamp(windowMinutes, 2, 120));
            return Snapshot(state, cutoff, threshold);
        }
    }

    public static void MarkCleanExit()
    {
        lock (Gate)
        {
            var state = Load();
            state.UncleanStartsUtc.Clear();
            state.LastCleanExitUtc = DateTime.UtcNow;
            Save(state);
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex) { DiagnosticsLog.Error("Failed to reset startup health history", ex); }
        }
    }

    private static StartupHealthSnapshot Snapshot(StartupHealthState state, DateTime cutoff, int threshold)
    {
        var recent = state.UncleanStartsUtc.Where(x => x >= cutoff).OrderBy(x => x).ToArray();
        var safeThreshold = Math.Clamp(threshold, 2, 10);
        return new StartupHealthSnapshot(recent.Length, recent.LastOrDefault() == default ? null : recent.Last(), recent.Length >= safeThreshold);
    }

    private static StartupHealthState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new StartupHealthState();
            return JsonSerializer.Deserialize<StartupHealthState>(File.ReadAllText(FilePath), Json) ?? new StartupHealthState();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to read startup health history", ex);
            return new StartupHealthState();
        }
    }

    private static void Save(StartupHealthState state)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception ex) { DiagnosticsLog.Error("Failed to save startup health history", ex); }
    }
}
