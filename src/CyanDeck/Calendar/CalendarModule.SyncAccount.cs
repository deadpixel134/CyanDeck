using System.Windows.Threading;

namespace CyanDeck.Calendar;

// Prompt upload after local changes, silent interval sync, Google disconnect and
// local event backup. Kept apart from the window placement code above.
public sealed partial class CalendarModule
{
    private DispatcherTimer? _promptSyncTimer;
    private bool _promptSyncAfterCurrent;
    private bool _intervalSyncDeferred;

    // Managed deployment capability check supplied by the host (null = allowed).
    public Func<string, bool>? ManagedCapabilityAllowed { get; set; }

    private void WireSyncAccountHooks(CalendarWindow window)
    {
        window.LocalChangeCommitted = OnLocalCalendarChange;
        window.GoogleNeedsReauthorization = () => _google.NeedsReauthorization;
        window.HasGoogleAccount = () => _google.HasAccountConnection;
        window.DisconnectGoogle = DisconnectGoogleAsync;
        window.CanImportCalendarEvents = () => ManagedCapabilityAllowed?.Invoke("ImportExport.Import") != false;
        // The constructor's first render ran before these hooks existed: redraw once so
        // the upload markers follow the real Google connection state.
        window.RenderMonthWhenIdle();
    }

    // The window reports a saved, moved or deleted Google-linked event. Returns true
    // when an automatic upload follows in a few seconds (never a browser login).
    private bool OnLocalCalendarChange()
    {
        if (_disposed || !_google.CanSyncSilently) return false;
        SchedulePromptSync(CalendarPromptSyncPolicy.DebounceDelay);
        return true;
    }

    private void SchedulePromptSync(TimeSpan delay)
    {
        if (_disposed) return;
        if (_promptSyncTimer is null)
        {
            _promptSyncTimer = new DispatcherTimer();
            _promptSyncTimer.Tick += async (_, _) => await OnPromptSyncTickAsync();
        }
        _promptSyncTimer.Stop();
        _promptSyncTimer.Interval = delay;
        _promptSyncTimer.Start();
    }

    private async Task OnPromptSyncTickAsync()
    {
        _promptSyncTimer?.Stop();
        if (_disposed || _window is null) return;
        // A deferred interval sync keeps the interval rule (it may show a binding/quota message).
        var canSync = _intervalSyncDeferred ? _google.CanSyncInBackground : _google.CanSyncSilently;
        var decision = CalendarPromptSyncPolicy.Decide(canSync,
            _intervalSyncDeferred || CalendarPromptSyncPolicy.HasPendingUploads(_events.Events),
            _window.IsGoogleConnecting, _window.IsEditing, _window.IsMovingEvent, _window.IsSynchronizing);
        if (decision == CalendarPromptSyncDecision.Skip)
        {
            _intervalSyncDeferred = false;
            return;
        }
        if (decision == CalendarPromptSyncDecision.RetryLater)
        {
            // A running sync picks this up once more when it finishes.
            if (_window.IsSynchronizing) _promptSyncAfterCurrent = true;
            else SchedulePromptSync(CalendarPromptSyncPolicy.BusyRetryDelay);
            return;
        }
        _intervalSyncDeferred = false;
        await SynchronizeAsync();
    }

    private async Task OnAutoSyncTickAsync()
    {
        // Never opens a browser login; stops after sign-in expiry until the user reconnects.
        if (_disposed || _window is null || !_google.CanSyncInBackground || _window.IsGoogleConnecting) return;
        if (_window.IsSynchronizing) return;
        if (_window.IsEditing || _window.IsMovingEvent)
        {
            // Do not interrupt the user; run once they finish.
            _intervalSyncDeferred = true;
            SchedulePromptSync(CalendarPromptSyncPolicy.BusyRetryDelay);
            return;
        }
        await SynchronizeAsync();
    }

    private void AfterSynchronizeAttempt()
    {
        if (!_promptSyncAfterCurrent || _disposed) return;
        _promptSyncAfterCurrent = false;
        SchedulePromptSync(CalendarPromptSyncPolicy.DebounceDelay);
    }

    private async Task<GoogleDisconnectResult> DisconnectGoogleAsync()
    {
        _promptSyncTimer?.Stop();
        _promptSyncAfterCurrent = false;
        _intervalSyncDeferred = false;
        return await _google.DisconnectAsync();
    }

    // Companion copy of the local-only events for a layout/settings export.
    public int ExportLocalEventsCompanion(string configurationPath)
        => CalendarEventBackup.ExportCompanion(_events.Events, configurationPath,
            eventsUnavailable: _events.IsReadOnly && !_events.IsRecoveredFromBackup);
}
