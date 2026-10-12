using System;

namespace DesktopDeck.Services;

/// <summary>
/// Makes feature preference persistence the commit boundary for runtime changes.
/// A failed settings save must not flip either module in the current process or
/// restart it into a mode that cannot be recovered from disk.
/// </summary>
internal static class FeatureSwitchCheckpoint
{
    internal enum Outcome { Unchanged, Applied, SaveFailed }

    internal static Outcome Apply(
        Func<bool> readCurrent,
        Action<bool> setCurrent,
        bool requested,
        Action save,
        Action<Exception> onSaveFailure)
    {
        ArgumentNullException.ThrowIfNull(readCurrent);
        ArgumentNullException.ThrowIfNull(setCurrent);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(onSaveFailure);

        var previous = readCurrent();
        if (previous == requested) return Outcome.Unchanged;
        setCurrent(requested);
        try { save(); }
        catch (Exception ex)
        {
            setCurrent(previous);
            onSaveFailure(ex);
            return Outcome.SaveFailed;
        }
        return Outcome.Applied;
    }
}
