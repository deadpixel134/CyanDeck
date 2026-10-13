using System;

namespace CyanDeck.Peek;

/// <summary>
/// Coalesces the WM_ACTIVATE / SC_RESTORE pair that one taskbar click can generate.
/// This is not a timer and does not schedule work; it only compares event timestamps.
/// </summary>
public sealed class PeekTaskbarActivationGate
{
    private DateTimeOffset _lastAcceptedUtc = DateTimeOffset.MinValue;

    public bool TryAccept(DateTimeOffset nowUtc, int debounceMilliseconds)
    {
        var debounce = TimeSpan.FromMilliseconds(Math.Clamp(debounceMilliseconds, 150, 1000));
        if (_lastAcceptedUtc != DateTimeOffset.MinValue && nowUtc - _lastAcceptedUtc < debounce)
        {
            return false;
        }

        _lastAcceptedUtc = nowUtc;
        return true;
    }

    public void Reset() => _lastAcceptedUtc = DateTimeOffset.MinValue;
}
