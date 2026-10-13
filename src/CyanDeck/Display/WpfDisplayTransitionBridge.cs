using System;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CyanDeck.Display;

/// <summary>
/// Connects Windows/WPF display, power and session notifications to the pure stabilizer.
/// The bridge intentionally does not create a DispatcherTimer. requestSettleCheck must
/// reuse CyanDeck's existing one-shot display-change debounce scheduler.
/// </summary>
internal sealed class WpfDisplayTransitionBridge : IDisposable
{
    internal const int WmDisplayChange = 0x007E;
    internal const int WmDpiChanged = 0x02E0;

    private readonly Dispatcher _dispatcher;
    private readonly DisplayTransitionStabilizer _stabilizer;
    private readonly Func<DisplayTopologySnapshot> _captureTopology;
    private readonly Action<int, long> _requestSettleCheck;
    private readonly Action<DisplayTopologySnapshot, DisplayTransitionRestoreReason> _restoreStableLayout;
    private readonly Action _flushDeferredLayoutSave;
    private readonly Action? _cancelTransientInteractions;
    private readonly Action<string>? _diagnostic;

    private bool _deferredLayoutSave;
    private bool _disposed;

    internal WpfDisplayTransitionBridge(
        Dispatcher dispatcher,
        Func<DisplayTopologySnapshot> captureTopology,
        Action<int, long> requestSettleCheck,
        Action<DisplayTopologySnapshot, DisplayTransitionRestoreReason> restoreStableLayout,
        Action flushDeferredLayoutSave,
        Action? cancelTransientInteractions = null,
        Action<string>? diagnostic = null,
        DisplayTransitionStabilizerOptions? options = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _captureTopology = captureTopology ?? throw new ArgumentNullException(nameof(captureTopology));
        _requestSettleCheck = requestSettleCheck ?? throw new ArgumentNullException(nameof(requestSettleCheck));
        _restoreStableLayout = restoreStableLayout ?? throw new ArgumentNullException(nameof(restoreStableLayout));
        _flushDeferredLayoutSave = flushDeferredLayoutSave ?? throw new ArgumentNullException(nameof(flushDeferredLayoutSave));
        _cancelTransientInteractions = cancelTransientInteractions;
        _diagnostic = diagnostic;
        _stabilizer = new DisplayTransitionStabilizer(options);

        SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    internal bool ShouldDeferLayoutWrites => _stabilizer.ShouldDeferLayoutWrites;
    internal bool IsStableForUserLayoutAction => !_disposed && _stabilizer.Phase == DisplayTransitionPhase.Stable && !_stabilizer.ShouldDeferLayoutWrites;
    internal DisplayTransitionPhase Phase => _stabilizer.Phase;
    internal long Generation => _stabilizer.Generation;

    /// <summary>
    /// Call at the start of the existing state-save path. When true, skip the physical
    /// write for now. The bridge will request one flush after stable restore completes.
    /// </summary>
    internal bool DeferLayoutSaveIfTransitioning()
    {
        if (!_stabilizer.ShouldDeferLayoutWrites)
        {
            return false;
        }

        _deferredLayoutSave = true;
        return true;
    }

    /// <summary>
    /// Forward WM_DISPLAYCHANGE / WM_DPICHANGED from the existing HwndSource hook.
    /// No extra native hook is installed by this overlay.
    /// </summary>
    internal void HandleWindowMessage(int message)
    {
        if (_disposed)
        {
            return;
        }

        if (message == WmDisplayChange)
        {
            DispatchSignal(DisplayTransitionSignal.WindowDisplayChanged);
        }
        else if (message == WmDpiChanged)
        {
            DispatchSignal(DisplayTransitionSignal.WindowDpiChanged);
        }
    }

    /// <summary>
    /// Invoke from CyanDeck's existing display-change debounce callback.
    /// generation is the token supplied to requestSettleCheck; stale callbacks are cheap
    /// no-ops and cannot commit an older topology.
    /// </summary>
    internal void OnScheduledSettleCheck(long generation)
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnScheduledSettleCheck(generation)));
            return;
        }

        if (generation != _stabilizer.Generation)
        {
            _diagnostic?.Invoke($"display transition: ignored stale settle check generation={generation}, current={_stabilizer.Generation}");
            return;
        }

        DisplayTopologySnapshot snapshot;
        try
        {
            snapshot = _captureTopology().Normalize();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            _diagnostic?.Invoke($"display transition: topology capture deferred ({ex.GetType().Name})");
            snapshot = DisplayTopologySnapshot.Invalid;
        }

        ApplyAction(_stabilizer.Evaluate(snapshot, Environment.TickCount64));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanging -= OnDisplaySettingsChanging;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private void OnDisplaySettingsChanging(object? sender, EventArgs e) =>
        DispatchSignal(DisplayTransitionSignal.DisplaySettingsChanging);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        DispatchSignal(DisplayTransitionSignal.DisplaySettingsChanged);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                DispatchSignal(DisplayTransitionSignal.Suspend);
                break;
            case PowerModes.Resume:
                DispatchSignal(DisplayTransitionSignal.Resume);
                break;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        DisplayTransitionSignal signal = e.Reason switch
        {
            SessionSwitchReason.SessionLock => DisplayTransitionSignal.SessionLock,
            SessionSwitchReason.SessionUnlock => DisplayTransitionSignal.SessionUnlock,
            SessionSwitchReason.RemoteConnect => DisplayTransitionSignal.RemoteConnect,
            SessionSwitchReason.RemoteDisconnect => DisplayTransitionSignal.RemoteDisconnect,
            SessionSwitchReason.ConsoleConnect => DisplayTransitionSignal.ConsoleConnect,
            SessionSwitchReason.ConsoleDisconnect => DisplayTransitionSignal.ConsoleDisconnect,
            SessionSwitchReason.SessionRemoteControl => DisplayTransitionSignal.SessionRemoteControl,
            _ => DisplayTransitionSignal.None,
        };

        if (signal != DisplayTransitionSignal.None)
        {
            DispatchSignal(signal);
        }
    }

    private void DispatchSignal(DisplayTransitionSignal signal)
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => DispatchSignal(signal)));
            return;
        }

        if (signal is DisplayTransitionSignal.DisplaySettingsChanging or
            DisplayTransitionSignal.WindowDisplayChanged or
            DisplayTransitionSignal.WindowDpiChanged or
            DisplayTransitionSignal.Suspend or
            DisplayTransitionSignal.SessionLock or
            DisplayTransitionSignal.RemoteConnect or
            DisplayTransitionSignal.RemoteDisconnect or
            DisplayTransitionSignal.ConsoleConnect or
            DisplayTransitionSignal.ConsoleDisconnect or
            DisplayTransitionSignal.SessionRemoteControl)
        {
            _cancelTransientInteractions?.Invoke();
        }

        ApplyAction(_stabilizer.Signal(signal, Environment.TickCount64));
        _diagnostic?.Invoke($"display transition: signal={signal}, phase={_stabilizer.Phase}, generation={_stabilizer.Generation}");
    }

    private void ApplyAction(DisplayTransitionAction action)
    {
        switch (action.Kind)
        {
            case DisplayTransitionActionKind.ScheduleCheck:
                _requestSettleCheck(action.DelayMilliseconds, action.Generation);
                break;

            case DisplayTransitionActionKind.StableRestore:
                bool restoreSucceeded = false;
                try
                {
                    _restoreStableLayout(action.Snapshot, action.RestoreReason);
                    restoreSucceeded = true;
                }
                finally
                {
                    if (restoreSucceeded && _deferredLayoutSave)
                    {
                        _deferredLayoutSave = false;
                        _flushDeferredLayoutSave();
                    }
                }
                _diagnostic?.Invoke(
                    $"display transition: stable restore generation={action.Generation}, reason={action.RestoreReason}, monitors={action.Snapshot.MonitorCount}, remote={action.Snapshot.IsRemoteSession}");
                break;
        }
    }
}
