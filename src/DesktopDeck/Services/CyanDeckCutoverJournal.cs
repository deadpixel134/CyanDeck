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
        // A journal is metadata, never a profile or arbitrary external file.
        // Reject links and oversized/corrupt records before deserialization.
        var info = new FileInfo(JournalPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 16 * 1024)
            throw new InvalidDataException("Unsafe CyanDeck migration journal.");
        Entry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(JournalPath));
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Malformed CyanDeck migration journal.", error);
        }
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
        Record(Aborted);
    }

    public static void Record(string phase)
    {
        if (!IsKnownPhase(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        var directory = Path.GetDirectoryName(JournalPath)!;
        Directory.CreateDirectory(directory);
        // Hold an exclusive lock across the read, validation and atomic rename.
        // Concurrent processes must not overwrite a newer journal transition.
        using var gate = new FileStream(JournalPath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var previous = Read();
        // Enforce transitions while holding the lock; abort is idempotent.
        if (phase == Aborted && (previous is null || previous.Phase == Aborted)) return;
        if (phase == Prepared && previous is not null && previous.Phase != Aborted)
            throw new InvalidOperationException("An existing migration must be aborted before preparing again.");
        if (phase == Verified)
        {
            if (previous is null || previous.Phase != Prepared)
                throw new InvalidOperationException("Verification requires a prepared migration journal.");
            if (!DataRootPaths.IsPreparedCopyIdentical())
                throw new InvalidOperationException("Prepared profile no longer matches legacy data.");
        }
        var temporary = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var entry = new Entry(CurrentVersion, phase, DateTimeOffset.UtcNow,
                DataRootPaths.LegacyRoaming, DataRootPaths.CyanRoaming);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, entry);
                output.Flush(flushToDisk: true);
            }
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
