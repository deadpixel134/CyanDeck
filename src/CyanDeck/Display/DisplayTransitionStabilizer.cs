using System;

namespace CyanDeck.Display;

/// <summary>
/// Coalesces display, DPI, power, and session churn into one stable-layout restore.
/// This type owns no timers, monitor enumeration, Registry access, or window hooks.
/// The host reuses its existing one-shot display-change debounce scheduler and supplies
/// topology snapshots from the existing monitor-profile subsystem.
/// </summary>
internal sealed class DisplayTransitionStabilizer
{
    private readonly DisplayTransitionStabilizerOptions _options;

    private DisplayTransitionPhase _phase = DisplayTransitionPhase.Stable;
    private long _generation;
    private long _transitionStartedMilliseconds;
    private long _lastSignalMilliseconds;
    private long _candidateSinceMilliseconds;
    private DisplayTopologySnapshot _candidate = DisplayTopologySnapshot.Invalid;
    private DisplayTopologySnapshot _latestValid = DisplayTopologySnapshot.Invalid;
    private bool _restorePending;
    private bool _suspended;
    private bool _sessionInactive;

    internal DisplayTransitionStabilizer(DisplayTransitionStabilizerOptions? options = null)
    {
        _options = (options ?? new DisplayTransitionStabilizerOptions()).Normalize();
    }

    internal DisplayTransitionPhase Phase => _phase;
    internal long Generation => _generation;
    internal bool ShouldDeferLayoutWrites => _phase != DisplayTransitionPhase.Stable;
    internal bool RestorePending => _restorePending;

    internal DisplayTransitionAction Signal(DisplayTransitionSignal signal, long timestampMilliseconds)
    {
        timestampMilliseconds = NormalizeTimestamp(timestampMilliseconds);

        switch (signal)
        {
            case DisplayTransitionSignal.Suspend:
                _suspended = true;
                EnterBlocked(DisplayTransitionPhase.Suspended, timestampMilliseconds);
                return DisplayTransitionAction.None(_generation);

            case DisplayTransitionSignal.SessionLock:
            case DisplayTransitionSignal.ConsoleDisconnect:
            case DisplayTransitionSignal.RemoteDisconnect:
                _sessionInactive = true;
                EnterBlocked(DisplayTransitionPhase.SessionInactive, timestampMilliseconds);
                return DisplayTransitionAction.None(_generation);

            case DisplayTransitionSignal.Resume:
                _suspended = false;
                return ResumeSettlingIfInteractive(timestampMilliseconds);

            case DisplayTransitionSignal.SessionUnlock:
            case DisplayTransitionSignal.RemoteConnect:
            case DisplayTransitionSignal.ConsoleConnect:
                _sessionInactive = false;
                return ResumeSettlingIfInteractive(timestampMilliseconds);

            case DisplayTransitionSignal.SessionRemoteControl:
                if (_suspended || _sessionInactive)
                {
                    _restorePending = true;
                    return DisplayTransitionAction.None(_generation);
                }
                BeginOrExtendSettling(timestampMilliseconds);
                return DisplayTransitionAction.Schedule(
                    _generation,
                    _options.InitialSettleDelayMilliseconds);

            case DisplayTransitionSignal.DisplaySettingsChanging:
            case DisplayTransitionSignal.DisplaySettingsChanged:
            case DisplayTransitionSignal.WindowDisplayChanged:
            case DisplayTransitionSignal.WindowDpiChanged:
                if (_suspended || _sessionInactive)
                {
                    _lastSignalMilliseconds = timestampMilliseconds;
                    _restorePending = true;
                    ResetCandidate();
                    return DisplayTransitionAction.None(_generation);
                }

                BeginOrExtendSettling(timestampMilliseconds);
                return DisplayTransitionAction.Schedule(
                    _generation,
                    _options.InitialSettleDelayMilliseconds);

            case DisplayTransitionSignal.None:
            default:
                return DisplayTransitionAction.None(_generation);
        }
    }

    internal DisplayTransitionAction Evaluate(
        DisplayTopologySnapshot snapshot,
        long timestampMilliseconds)
    {
        timestampMilliseconds = NormalizeTimestamp(timestampMilliseconds);
        snapshot = snapshot.Normalize();

        if (_phase == DisplayTransitionPhase.Stable || !_restorePending)
        {
            return DisplayTransitionAction.None(_generation);
        }

        if (_suspended || _sessionInactive ||
            _phase is DisplayTransitionPhase.Suspended or DisplayTransitionPhase.SessionInactive)
        {
            return DisplayTransitionAction.None(_generation);
        }

        if (snapshot.IsValid)
        {
            _latestValid = snapshot;
        }

        long elapsed = NonNegativeDifference(_transitionStartedMilliseconds, timestampMilliseconds);
        if (elapsed >= _options.MaximumSettleMilliseconds && _latestValid.IsValid)
        {
            return Complete(DisplayTransitionRestoreReason.MaximumSettleElapsed, _latestValid);
        }

        if (!snapshot.IsValid)
        {
            ResetCandidate();
            return DisplayTransitionAction.Schedule(_generation, _options.RetryDelayMilliseconds);
        }

        long sinceLastSignal = NonNegativeDifference(_lastSignalMilliseconds, timestampMilliseconds);
        if (sinceLastSignal < _options.InitialSettleDelayMilliseconds)
        {
            return DisplayTransitionAction.Schedule(
                _generation,
                _options.InitialSettleDelayMilliseconds - (int)sinceLastSignal);
        }

        if (!_candidate.IsValid || !Equivalent(_candidate, snapshot))
        {
            _candidate = snapshot;
            _candidateSinceMilliseconds = timestampMilliseconds;
            return DisplayTransitionAction.Schedule(_generation, _options.StableTopologyWindowMilliseconds);
        }

        long candidateAge = NonNegativeDifference(_candidateSinceMilliseconds, timestampMilliseconds);
        if (candidateAge < _options.StableTopologyWindowMilliseconds)
        {
            return DisplayTransitionAction.Schedule(
                _generation,
                _options.StableTopologyWindowMilliseconds - (int)candidateAge);
        }

        return Complete(DisplayTransitionRestoreReason.StableTopology, snapshot);
    }

    internal void ResetToStable()
    {
        _phase = DisplayTransitionPhase.Stable;
        _restorePending = false;
        _transitionStartedMilliseconds = 0;
        _lastSignalMilliseconds = 0;
        _latestValid = DisplayTopologySnapshot.Invalid;
        _suspended = false;
        _sessionInactive = false;
        ResetCandidate();
    }

    private void EnterBlocked(DisplayTransitionPhase phase, long timestampMilliseconds)
    {
        _generation++;
        _phase = phase;
        _lastSignalMilliseconds = timestampMilliseconds;
        _restorePending = true;
        ResetCandidate();
        // The settle timeout must start after the session becomes interactive again,
        // not while the machine is asleep or disconnected.
        _transitionStartedMilliseconds = 0;
        _latestValid = DisplayTopologySnapshot.Invalid;
    }

    private DisplayTransitionAction ResumeSettlingIfInteractive(long timestampMilliseconds)
    {
        if (_suspended)
        {
            _phase = DisplayTransitionPhase.Suspended;
            _restorePending = true;
            return DisplayTransitionAction.None(_generation);
        }

        if (_sessionInactive)
        {
            _phase = DisplayTransitionPhase.SessionInactive;
            _restorePending = true;
            return DisplayTransitionAction.None(_generation);
        }

        BeginFreshSettling(timestampMilliseconds);
        return DisplayTransitionAction.Schedule(_generation, _options.InitialSettleDelayMilliseconds);
    }

    private void BeginFreshSettling(long timestampMilliseconds)
    {
        _generation++;
        _phase = DisplayTransitionPhase.Settling;
        _lastSignalMilliseconds = timestampMilliseconds;
        _transitionStartedMilliseconds = timestampMilliseconds;
        _restorePending = true;
        _latestValid = DisplayTopologySnapshot.Invalid;
        ResetCandidate();
    }

    private void BeginOrExtendSettling(long timestampMilliseconds)
    {
        bool wasStable = _phase == DisplayTransitionPhase.Stable;
        _generation++;
        _phase = DisplayTransitionPhase.Settling;
        _lastSignalMilliseconds = timestampMilliseconds;
        _restorePending = true;
        ResetCandidate();

        if (wasStable || _transitionStartedMilliseconds <= 0)
        {
            _transitionStartedMilliseconds = timestampMilliseconds;
            _latestValid = DisplayTopologySnapshot.Invalid;
        }
    }

    private DisplayTransitionAction Complete(
        DisplayTransitionRestoreReason reason,
        DisplayTopologySnapshot snapshot)
    {
        long generation = _generation;
        _phase = DisplayTransitionPhase.Stable;
        _restorePending = false;
        _transitionStartedMilliseconds = 0;
        _lastSignalMilliseconds = 0;
        _candidateSinceMilliseconds = 0;
        _candidate = DisplayTopologySnapshot.Invalid;
        _latestValid = snapshot;
        return DisplayTransitionAction.Restore(generation, reason, snapshot);
    }

    private void ResetCandidate()
    {
        _candidate = DisplayTopologySnapshot.Invalid;
        _candidateSinceMilliseconds = 0;
    }

    private static bool Equivalent(DisplayTopologySnapshot left, DisplayTopologySnapshot right) =>
        left.IsValid &&
        right.IsValid &&
        left.MonitorCount == right.MonitorCount &&
        left.IsRemoteSession == right.IsRemoteSession &&
        string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal);

    private static long NormalizeTimestamp(long value) => value < 0 ? 0 : value;

    private static long NonNegativeDifference(long earlier, long later)
    {
        if (later <= earlier)
        {
            return 0;
        }

        return later - earlier;
    }
}
