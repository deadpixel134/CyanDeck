using System.Threading;

namespace DesktopDeck.Services;

// Keep this name stable while DesktopDeck.exe and CyanDeck.exe can both
// write the legacy profile. A clean exit must explicitly release ownership.
public sealed class LegacySingleInstanceGate : IDisposable
{
    public const string MutexName = @"Local\DesktopDeck.SingleInstance";

    private readonly Mutex _mutex;
    private bool _owns;

    private LegacySingleInstanceGate(Mutex mutex, bool owns)
    {
        _mutex = mutex;
        _owns = owns;
    }

    public bool IsFirst => _owns;

    public static LegacySingleInstanceGate TryAcquire() => TryAcquire(MutexName);

    // Isolate integration tests from an installed application's mutex.
    internal static LegacySingleInstanceGate TryAcquire(string mutexName)
    {
        var mutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out var createdNew);
        return new LegacySingleInstanceGate(mutex, createdNew);
    }

    public void Dispose()
    {
        if (_owns)
        {
            // Non-owning second instances must never release the real
            // application's mutex. WPF disposes on the owning UI thread.
            _owns = false;
            try { _mutex.ReleaseMutex(); }
            finally { _mutex.Dispose(); }
        }
        else
        {
            _mutex.Dispose();
        }
    }
}
