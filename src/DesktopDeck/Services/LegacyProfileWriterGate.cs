namespace DesktopDeck.Services;

// Cross-executable write lease for the *legacy* roaming/local data profile.
// Keep this named mutex unchanged during DesktopDeck -> CyanDeck renaming.
// A future CyanDeck.exe that could touch the legacy profile must acquire it,
// even when its own single-instance/activation identity changes.
//
// Older DesktopDeck releases do not know this mutex; the migration tool also
// holds Local\DesktopDeck.SingleInstance to exclude those binaries during
// preparation. This lease alone does not authorize any actual profile cutover.
public sealed class LegacyProfileWriterGate : IDisposable
{
    public const string MutexName = @"Local\CyanDeck.LegacyProfileWriter.v1";

    private readonly Mutex _mutex;
    private bool _owned;

    private LegacyProfileWriterGate(Mutex mutex)
    {
        _mutex = mutex;
        _owned = true;
    }

    public static LegacyProfileWriterGate Acquire()
    {
        var mutex = new Mutex(initiallyOwned: false, name: MutexName);
        try
        {
            if (!mutex.WaitOne(0))
                throw new InvalidOperationException(
                    "Another DesktopDeck/CyanDeck writer holds the legacy profile. " +
                    "Close the other application before starting.");
            return new LegacyProfileWriterGate(mutex);
        }
        catch (AbandonedMutexException ex)
        {
            // WaitOne grants ownership even when the previous writer crashed.
            // Release it, fail closed for this startup, and require a retry.
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw new InvalidOperationException(
                "A previous legacy-profile writer terminated unexpectedly. " +
                "Review its data before reopening CyanDeck.", ex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    // Acquired and released on the WPF UI thread. Keep held through the
    // AppController.Dispose() flush of pending layout/calendar writes.
    public void Dispose()
    {
        if (!_owned) return;
        _owned = false;
        try { _mutex.ReleaseMutex(); }
        finally { _mutex.Dispose(); }
    }
}
