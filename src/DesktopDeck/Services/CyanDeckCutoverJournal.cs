using System.Text.Json;

namespace DesktopDeck.Services;

// A durable, non-activating cutover audit record. Never store OAuth secrets here.
// This journal is deliberately separate from the profile being copied.
public static class CyanDeckCutoverJournal
{
    public const int CurrentVersion = 1;
    public const string Prepared = "prepared";
    public const string Verified = "verified";
    public const string Aborted = "aborted";

    public sealed record Entry(int Version, string Phase, DateTimeOffset RecordedAtUtc,
        string LegacyRoot, string CandidateRoot);

    public static string JournalPath => Path.Combine(DataRootPaths.LegacyLocal,
        "migration", "cyandeck-cutover-state.json");

    public static Entry? Read()
    {
        if (!File.Exists(JournalPath)) return null;
        var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(JournalPath));
        if (entry is null || entry.Version != CurrentVersion || !IsKnownPhase(entry.Phase) ||
            !string.Equals(entry.LegacyRoot, DataRootPaths.LegacyRoaming, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.CandidateRoot, DataRootPaths.CyanRoaming, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unrecognized CyanDeck migration journal; refusing automatic activation.");
        return entry;
    }

    // A verified journal is only evidence of an earlier check. Revalidate
    // against the current legacy files before reporting readiness for review.
    // This never changes the active profile or the journal on disk.
    public static bool IsCurrentlyVerifiedForReview()
    {
        var entry = Read();
        return entry is not null && entry.Phase == Verified &&
            DataRootPaths.IsPreparedCopyIdentical();
    }

    // Explicit rollback-preparation record. This does not move or delete data;
    // the legacy profile remains active throughout the current release.
    public static void AbortPreparation()
    {
        var previous = Read();
        if (previous is null || previous.Phase == Aborted) return;
        Record(Aborted);
    }

    public static void Record(string phase)
    {
        if (!IsKnownPhase(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (phase == Verified && !DataRootPaths.IsPreparedCopyIdentical())
            throw new InvalidOperationException("Prepared profile no longer matches legacy data.");
        var directory = Path.GetDirectoryName(JournalPath)!;
        Directory.CreateDirectory(directory);
        var temporary = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var entry = new Entry(CurrentVersion, phase, DateTimeOffset.UtcNow,
                DataRootPaths.LegacyRoaming, DataRootPaths.CyanRoaming);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry));
            File.Move(temporary, JournalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsKnownPhase(string phase) =>
        phase is Prepared or Verified or Aborted;
}
