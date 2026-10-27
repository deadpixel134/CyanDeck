namespace CyanDeck.Services;

/// <summary>
/// v8.0.44: commands the Settings window needs for update/backup/recovery controls that only
/// the running AppController can perform (they confirm, write checkpoints or restart). The
/// controller registers them at startup so Settings does not need new constructor arguments.
/// Each delegate shows its own confirmation and result message. Null when not running.
/// </summary>
public static class BackupRecoveryCommands
{
    // Restores one recovery checkpoint file after showing what changes (true when restored).
    public static Func<string, bool>? RestoreRecoveryCheckpoint { get; private set; }

    // Deletes unused update backups/downloads after confirmation (true when something was removed).
    public static Func<bool>? CleanUpdateBackups { get; private set; }

    // Releases the "rolled-back version is not reinstalled automatically" hold (true when released).
    public static Func<bool>? ReleaseRollbackHold { get; private set; }

    internal static void Register(Func<string, bool> restoreRecoveryCheckpoint, Func<bool> cleanUpdateBackups, Func<bool> releaseRollbackHold)
    {
        RestoreRecoveryCheckpoint = restoreRecoveryCheckpoint;
        CleanUpdateBackups = cleanUpdateBackups;
        ReleaseRollbackHold = releaseRollbackHold;
    }
}
