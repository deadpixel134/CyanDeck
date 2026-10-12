using System;

namespace DesktopDeck.Display;

internal enum DisplayTransitionSignal
{
    None = 0,
    DisplaySettingsChanging,
    DisplaySettingsChanged,
    WindowDisplayChanged,
    WindowDpiChanged,
    Suspend,
    Resume,
    SessionLock,
    SessionUnlock,
    RemoteConnect,
    RemoteDisconnect,
    ConsoleConnect,
    ConsoleDisconnect,
    SessionRemoteControl,
}

internal enum DisplayTransitionPhase
{
    Stable = 0,
    Settling,
    Suspended,
    SessionInactive,
}

internal enum DisplayTransitionActionKind
{
    None = 0,
    ScheduleCheck,
    StableRestore,
}

internal enum DisplayTransitionRestoreReason
{
    None = 0,
    StableTopology,
    MaximumSettleElapsed,
}

internal readonly record struct DisplayTopologySnapshot(
    string Fingerprint,
    int MonitorCount,
    bool IsRemoteSession,
    bool IsValid)
{
    internal static DisplayTopologySnapshot Invalid => new(string.Empty, 0, false, false);

    internal DisplayTopologySnapshot Normalize()
    {
        string fingerprint = (Fingerprint ?? string.Empty).Trim();
        int monitorCount = Math.Max(0, MonitorCount);
        bool valid = IsValid && monitorCount > 0 && fingerprint.Length > 0;
        return new DisplayTopologySnapshot(fingerprint, monitorCount, IsRemoteSession, valid);
    }
}

internal readonly record struct DisplayTransitionAction(
    DisplayTransitionActionKind Kind,
    int DelayMilliseconds,
    long Generation,
    DisplayTransitionRestoreReason RestoreReason,
    DisplayTopologySnapshot Snapshot)
{
    internal static DisplayTransitionAction None(long generation) =>
        new(DisplayTransitionActionKind.None, 0, generation, DisplayTransitionRestoreReason.None, DisplayTopologySnapshot.Invalid);

    internal static DisplayTransitionAction Schedule(long generation, int delayMilliseconds) =>
        new(DisplayTransitionActionKind.ScheduleCheck, Math.Max(1, delayMilliseconds), generation, DisplayTransitionRestoreReason.None, DisplayTopologySnapshot.Invalid);

    internal static DisplayTransitionAction Restore(
        long generation,
        DisplayTransitionRestoreReason reason,
        DisplayTopologySnapshot snapshot) =>
        new(DisplayTransitionActionKind.StableRestore, 0, generation, reason, snapshot);
}

internal sealed record DisplayTransitionStabilizerOptions
{
    // Time from the most recent display/session signal before the first topology sample.
    internal int InitialSettleDelayMilliseconds { get; init; } = 450;

    // Two matching topology samples must span at least this interval.
    internal int StableTopologyWindowMilliseconds { get; init; } = 650;

    // Retry cadence when the topology is temporarily invalid or changed again.
    internal int RetryDelayMilliseconds { get; init; } = 350;

    // Prevents an ordinary display transition from suppressing saves forever.
    // Suspended/locked sessions remain blocked until the corresponding resume/unlock signal.
    internal int MaximumSettleMilliseconds { get; init; } = 8000;

    internal DisplayTransitionStabilizerOptions Normalize() => this with
    {
        InitialSettleDelayMilliseconds = Math.Clamp(InitialSettleDelayMilliseconds, 100, 3000),
        StableTopologyWindowMilliseconds = Math.Clamp(StableTopologyWindowMilliseconds, 200, 3000),
        RetryDelayMilliseconds = Math.Clamp(RetryDelayMilliseconds, 100, 2000),
        MaximumSettleMilliseconds = Math.Clamp(MaximumSettleMilliseconds, 1500, 30000),
    };
}
