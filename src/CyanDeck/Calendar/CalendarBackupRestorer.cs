namespace CyanDeck.Calendar;

/// <summary>
/// Performs an explicitly requested recovery from the already validated .bak
/// convention. A damaged primary is archived instead of destroyed. The backup
/// remains untouched, even after a successful recovery.
/// </summary>
internal static class CalendarBackupRestorer
{
    internal static (T Snapshot, string? ArchivedPrimary) Restore<T>(
        string primaryPath, Func<string, T> validateAndRead)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentNullException.ThrowIfNull(validateAndRead);
        var backupPath = primaryPath + ".bak";
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("복원할 캘린더 백업 파일이 없습니다.", backupPath);

        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new InvalidOperationException("캘린더 파일 경로가 잘못되었습니다.");
        Directory.CreateDirectory(directory);
        // The staging copy and the original must be on the same volume so the
        // replacement of an existing primary is atomic on Windows.
        var staged = primaryPath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        string? archived = null;
        try
        {
            File.Copy(backupPath, staged);
            // Re-read the STAGED bytes: backup contents could have changed
            // since the application first entered read-only recovery mode.
            var snapshot = validateAndRead(staged);
            if (File.Exists(primaryPath))
            {
                archived = primaryPath + ".damaged-"
                    + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-"
                    + Guid.NewGuid().ToString("N");
                File.Replace(staged, primaryPath, archived, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(staged, primaryPath);
            }
            return (snapshot, archived);
        }
        finally
        {
            // Never remove an existing primary or the sole known-good backup
            // on error. The only disposable file is our own staging copy.
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
