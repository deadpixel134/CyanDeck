using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CyanDeck.Services;

internal enum RecoveryCheckpointBucket
{
    // Created automatically on dock/undock, monitor sleep or resolution changes.
    MonitorAutomatic,
    // Before update, import, reset, delete, rollback or an unclean session.
    Important,
    // Everything else keeps the previous "newest 12" behavior.
    Other
}

/// <summary>
/// v8.0.44: recovery checkpoints are retained per kind. Frequent automatic monitor
/// checkpoints keep only the newest 5 so they can no longer push out the checkpoints taken
/// before an update, import, reset, deletion or an unclean session, which keep the newest 20
/// plus anything from the last 30 days (bounded by a hard cap).
/// </summary>
internal static class RecoveryCheckpointRetentionPolicy
{
    public const int MonitorKeep = 5;
    public const int ImportantKeep = 20;
    public const int ImportantHardCap = 60;
    public const int OtherKeep = 12;
    public static readonly TimeSpan ImportantMaxAge = TimeSpan.FromDays(30);

    private static readonly string[] MonitorPrefixes =
    {
        "before-monitor-profile-restore", "before-auto-monitor-reduction"
    };

    private static readonly string[] ImportantPrefixes =
    {
        "unclean-session", "before-import", "before-factory-reset", "before-update",
        "before-github-auto-update", "before-startup-auto-update", "before-app-rollback",
        "before-recovery-restore", "before-automatic-layout", "before-geometry-reset",
        "before-integrity-check", "before-delete", "before-remove"
    };

    internal static RecoveryCheckpointBucket Classify(string? fileNameWithoutExtension)
    {
        var name = (fileNameWithoutExtension ?? "").Trim();
        if (MonitorPrefixes.Any(p => HasPrefix(name, p))) return RecoveryCheckpointBucket.MonitorAutomatic;
        if (ImportantPrefixes.Any(p => HasPrefix(name, p))) return RecoveryCheckpointBucket.Important;
        return RecoveryCheckpointBucket.Other;
    }

    internal static string DescribeBucket(RecoveryCheckpointBucket bucket) => bucket switch
    {
        RecoveryCheckpointBucket.MonitorAutomatic => "모니터 변경 자동 복구 지점",
        RecoveryCheckpointBucket.Important => "중요 작업 전 복구 지점",
        _ => "기타 복구 지점"
    };

    /// <summary>Returns the names (as given) that should be deleted.</summary>
    internal static IReadOnlyList<string> SelectForDeletion(IEnumerable<(string Name, DateTime LastWriteUtc)> files, DateTime utcNow)
    {
        var delete = new List<string>();
        foreach (var bucket in files.GroupBy(f => Classify(Path.GetFileNameWithoutExtension(f.Name))))
        {
            var ordered = bucket.OrderByDescending(f => f.LastWriteUtc).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var keep = bucket.Key switch
                {
                    RecoveryCheckpointBucket.MonitorAutomatic => index < MonitorKeep,
                    RecoveryCheckpointBucket.Important => index < ImportantKeep ||
                        (index < ImportantHardCap && utcNow - ordered[index].LastWriteUtc <= ImportantMaxAge),
                    _ => index < OtherKeep
                };
                if (!keep) delete.Add(ordered[index].Name);
            }
        }
        return delete;
    }

    private static bool HasPrefix(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        // "before-importer" is a different reason than "before-import".
        return name.Length == prefix.Length || name[prefix.Length] == '-';
    }
}

internal sealed record RecoveryCheckpointEntry(string Path, string Label, DateTime LastWriteLocal, RecoveryCheckpointBucket Bucket)
{
    public string BucketLabel => RecoveryCheckpointRetentionPolicy.DescribeBucket(Bucket);
    public string DisplayText => $"{LastWriteLocal:yyyy-MM-dd HH:mm} · {Label}";
}

/// <summary>Read-only listing of every recovery checkpoint for Settings and the tray.</summary>
internal static class RecoveryCheckpointCatalog
{
    internal static IReadOnlyList<RecoveryCheckpointEntry> List(string recoveryFolder, int max = 200)
    {
        try
        {
            if (!Directory.Exists(recoveryFolder)) return Array.Empty<RecoveryCheckpointEntry>();
            return new DirectoryInfo(recoveryFolder).GetFiles("*.json")
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Math.Max(1, max))
                .Select(f =>
                {
                    var stem = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                    return new RecoveryCheckpointEntry(f.FullName, RecoveryCheckpointLabelPolicy.Describe(stem),
                        f.LastWriteTime, RecoveryCheckpointRetentionPolicy.Classify(stem));
                })
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<RecoveryCheckpointEntry>();
        }
    }
}
