using System.Windows.Threading;
using CyanDeck.Models;
using CyanDeck.Services;

namespace CyanDeck.Calendar;

// Feature lifecycle is completely separate from the Deck runtime. Safe Mode does
// not start this module; calendar-only mode uses the same DesktopHost instance.
public sealed partial class CalendarModule : IDisposable
{
    private readonly DesktopHost _host;
    private readonly DeckSettings _settings;
    private readonly Action _onSettingsChanged;
    private readonly bool _ownsShellRecovery;
    private readonly CalendarEventStore _events = new();
    private readonly CalendarPreferencesStore _preferences = new();
    private readonly GoogleCalendarSync _google;
    private readonly KoreanHolidayFeedService _holidayFeeds = new();
    private readonly CancellationTokenSource _holidayCancellation = new();
    private readonly SemaphoreSlim _holidayRefreshGate = new(1, 1);
    private DispatcherTimer? _holidayTimer;
    private CalendarWindow? _window;
    // Opt-in for this process only; never silently float over other apps.
    private bool _standaloneWindowMode;
    private DesktopShowDesktopRecovery? _showDesktopRecovery;
    private DesktopShellRecoveryScheduler? _shellScheduler;
    private DispatcherTimer? _syncTimer;
    private bool _disposed;
    private string _lastPlacementProblem = "";
    private bool _attachmentAttemptInProgress;
    private DateTime _lastAttachmentAttemptUtc = DateTime.MinValue;
    private DateTime _lastPlacementWarningUtc = DateTime.MinValue;
    // Once a placement failed, do not endlessly retry SetWindowPos/Show/Hide
    // on a six-second shell timer. A user-initiated retry or a REAL lost shell
    // HWND may clear this circuit breaker.
    private bool _automaticPlacementSuppressed;
    private DateTime _lastManualRepairUtc = DateTime.MinValue;
    private static readonly TimeSpan ManualRepairCooldown = TimeSpan.FromSeconds(15);
    // A transient access-denied result during Explorer/WPF startup must not hide
    // the calendar for the entire session. Retry only a few times, while the
    // calendar HWND remains invisible, and never restack on focus/activation.
    private static readonly TimeSpan[] DeferredPlacementDelays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2)
    ];
    private DispatcherTimer? _deferredPlacementTimer;
    private int _deferredPlacementAttempts;
    private DateTime _nextDeferredPlacementUtc = DateTime.MinValue;

    public CalendarModule(DesktopHost host, DeckSettings settings, Action onSettingsChanged, bool ownsShellRecovery)
    {
        _ownsShellRecovery = ownsShellRecovery;
        _host = host;
        _settings = settings;
        _onSettingsChanged = onSettingsChanged;
        _google = new GoogleCalendarSync(_events, _preferences,
            () => GoogleOAuthClientConfiguration.Resolve(_settings.GoogleCalendarClientId));
    }

    public void Start()
    {
        if (_disposed) return;
        if (_window is not null)
        {
            if (_standaloneWindowMode)
            {
                if (!_window.IsVisible) _window.Show();
            }
            else if (!_window.IsVisible) EnsureAttachedAfterShellRebuild();
            return;
        }
        _window = new CalendarWindow(_settings, _events, _preferences, _onSettingsChanged,
            SynchronizeAsync, (ct, force) => _google.LoadCalendarsAsync(ct, force), ApplyAutoSyncInterval,
            _holidayFeeds, RefreshHolidayFeedsAsync, standaloneWindowMode: _standaloneWindowMode);
        WireSyncAccountHooks(_window);
        if (_standaloneWindowMode)
        {
            _window.EnsureWithinVisibleWorkArea("standalone window start");
            _window.Opacity = double.IsFinite(_settings.CalendarOpacity)
                ? Math.Clamp(_settings.CalendarOpacity, 0.2, 1.0) : 0.86;
            _window.Show();
            DiagnosticsLog.Info("Calendar user-selected standalone window opened (normal taskbar/Z-order)");
        }
        else
        {
            // Safety boundary: focus/Activated events MUST NOT rewrite global desktop Z-order.
            // A WPF popup activation can happen while another application is focused;
            // never schedule window.Show()/Hide()/SetWindowPos as an activation side effect.
            _window.ShowActivated = false;
            _window.Opacity = 0;
            // The HWND must exist for MonitorFromWindow; this does not Show() it.
            // Restore a disconnected-monitor position or an oversize calendar before
            // any desktop Z-order attachment is attempted.
            _ = new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
            _window.EnsureWithinVisibleWorkArea("startup");
            // Probe placement while the HWND remains hidden. Only Show() if the desktop
            // band is validated, and never expose a normal floating window as fallback.
            EnsureAttachedAfterShellRebuild(force: true);
        }
        // A Show Desktop minimize is not an Explorer rebuild. Restore the HWND
        // once without activation, but never override a failed placement latch.
        if (!_standaloneWindowMode)
        {
            _showDesktopRecovery = new DesktopShowDesktopRecovery(_window,
            () => !_disposed && !_automaticPlacementSuppressed && _settings.EnableCalendar
                && _window is { IsEditing: false },
            () =>
            {
                if (_window is not null && !_host.IsInteractiveSurfaceAttached(_window))
                    EnsureAttachedAfterShellRebuild(force: true); // One bounded Win+D incident; fail closed.
            },
            () => _window is not null && _host.IsInteractiveSurfaceAttached(_window));
        }
        // Do not force the Explorer shell to manufacture another WorkerW when
        // startup placement fails. An explicit user action can retry once; actual
        // Explorer destruction can also reset the circuit breaker.
        // A backup is useful for viewing an older snapshot, not a license to
        // overwrite a damaged primary or replay stale Google sync tokens.
        if (_events.IsRecoveredFromBackup || _preferences.IsRecoveredFromBackup)
            _window.SetStatus("백업을 읽기 전용으로 표시 중입니다. 원본을 백업하고 데이터 복구 후 다시 실행하세요.");
        else if (_events.IsReadOnly || _preferences.IsReadOnly)
            _window.SetStatus("캘린더 저장 파일을 읽을 수 없습니다. 원본을 보존하고 로그를 확인하세요.");
        // In combined mode the existing AppController shell timer owns refreshes.
        // Running two identity probes against the same DesktopHost can swallow a
        // rebuild notification before Fence windows have been restored.
        if (_ownsShellRecovery && _shellScheduler is null)
        {
            _shellScheduler = new DesktopShellRecoveryScheduler(
                Dispatcher.CurrentDispatcher, TimeSpan.FromSeconds(6), () =>
            {
                if (_window is null || _window.IsEditing) return;
                try
                {
                    var changed = _host.RefreshDesktopShellIdentity(out var previousWasValid);
                    if (changed && !previousWasValid)
                        NotifyExplorerShellRebuilt();
                }
                catch (Exception ex) { DiagnosticsLog.Warn("Calendar shell recovery: " + ex.Message); }
            }, "Calendar-only shell recovery");
            _shellScheduler.Start();
        }
        _syncTimer?.Stop();
        _holidayTimer?.Stop();
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(CalendarSyncIntervalPolicy.DefaultMinutes) };
        // Silent only: never opens a browser login, pauses after sign-in expiry, and
        // waits for an open editor or an event drag to finish.
        _syncTimer.Tick += async (_, _) => await OnAutoSyncTickAsync();
        ApplyAutoSyncInterval();
        // Subscribed holiday calendars refresh independently of Google OAuth.
        // Cached copies are already available in the window on cold/offline start.
        _holidayTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(12) };
        _holidayTimer.Tick += async (_, _) => await RefreshHolidayFeedsAsync();
        _holidayTimer.Start();
        _ = RefreshHolidayFeedsAsync();
    }

    private async Task<string> RefreshHolidayFeedsAsync()
    {
        if (_disposed || _holidayCancellation.IsCancellationRequested) return "공휴일 갱신이 취소되었습니다.";
        try
        {
            // Do not create overlapping fetches when checkboxes and timer fire together.
            await _holidayRefreshGate.WaitAsync(_holidayCancellation.Token);
            try
            {
                if (_disposed) return "공휴일 갱신이 취소되었습니다.";
                await _holidayFeeds.RefreshAsync(_preferences.VisibleHolidayIds(), _holidayCancellation.Token);
            }
            finally { _holidayRefreshGate.Release(); }
            // Deferred while editing, in the settings panel or during an event drag.
            if (!_disposed) _window?.RenderMonthWhenIdle();
            return KoreanHolidayFeedService.DescribeResults(_holidayFeeds.LastResults);
        }
        catch (OperationCanceledException) when (_holidayCancellation.IsCancellationRequested)
        { return "공휴일 갱신이 취소되었습니다."; }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn("Holiday subscriptions refresh: " + ex.Message);
            return "공휴일 갱신 실패: " + ex.Message;
        }
    }

    // Apply immediately on save; never change window Z-order or force a Google login.
    // Restart the timer to count the newly selected interval from this moment.
    private void ApplyAutoSyncInterval()
    {
        if (_syncTimer is null || _disposed) return;
        _syncTimer.Stop();
        var minutes = CalendarSyncIntervalPolicy.Normalize(_preferences.Data.AutoSyncIntervalMinutes);
        if (minutes == 0) return;
        _syncTimer.Interval = TimeSpan.FromMinutes(minutes);
        _syncTimer.Start();
    }

    // Monitor topology may have changed even if the calendar cannot enter the
    // desktop band. Correct geometry independently, without Show/SetWindowPos.
    public void EnsureBoundsAfterDisplayTransition()
    {
        if (!_disposed && _window is { IsEditing: false } window)
            window.EnsureWithinVisibleWorkArea("display topology stabilized");
    }

    public void GoToToday() => _window?.GoToToday();
    public void ApplySettings() => _window?.ApplyViewAndAppearanceFromSettings();
    public bool IsSurfaceVisible => _window is { IsVisible: true } visible
        && visible.Opacity > 0 && (_standaloneWindowMode || _host.IsInteractiveSurfaceAttached(visible));

    /// <summary>
    /// User-directed escape hatch for machines which reject desktop Z-order
    /// insertion (Win32Error=5). This is a NORMAL non-topmost taskbar window,
    /// not an automatic overlay or an Explorer HWND modification.
    /// </summary>
    public bool TryOpenStandaloneWindow()
    {
        if (_disposed || !_settings.EnableCalendar) return false;
        if (_window is { IsEditing: true } || _window is { IsGoogleConnecting: true })
        {
            _lastPlacementProblem = "일정 편집 또는 Google 연결을 완료한 후 다시 시도하세요.";
            return false;
        }
        try
        {
            _deferredPlacementTimer?.Stop();
            _nextDeferredPlacementUtc = DateTime.MinValue;
            _showDesktopRecovery?.Dispose();
            _showDesktopRecovery = null;
            if (!_standaloneWindowMode)
            {
                if (_window is not null)
                {
                    _host.ForgetInteractiveSurface(_window);
                    _window.AllowFinalClose();
                    _window.Close();
                    _window = null;
                }
                _standaloneWindowMode = true;
            }
            Start();
            if (_window is null) return false;
            if (_window.WindowState == System.Windows.WindowState.Minimized)
                _window.WindowState = System.Windows.WindowState.Normal;
            if (!_window.IsVisible) _window.Show();
            _window.Activate(); // Only on the user's explicit tray action.
            return true;
        }
        catch (Exception ex)
        {
            _lastPlacementProblem = ex.Message;
            DiagnosticsLog.Error("Calendar standalone window fallback failed", ex);
            return false;
        }
    }

    // Tray status is a snapshot, not proof of actual rendered pixels. Avoid reporting
    // "display failure" solely because a transient WorkerW check changed after Show.
    public string GetTrayDisplayStatus()
    {
        var w = _window;
        if (w is null) return "창 시작 대기";
        if (_standaloneWindowMode)
            return w.IsVisible ? "일반 창 모드 (사용자 선택)" : "일반 창 숨김 (트레이에서 다시 열기)";
        if (!w.IsVisible || w.Opacity <= 0)
            return _deferredPlacementTimer?.IsEnabled == true
                ? "창 숨김 (자동 표시 복구 예약됨)"
                : "창 숨김 (트레이에서 일반 창 열기 또는 상태 진단)";
        if (_host.IsInteractiveSurfaceAttached(w))
            return "창 계층 연결됨 (화면 렌더링 미검증)";
        return "창 표시 중 (창 계층 확인 필요)";
    }

    // This is a diagnostic of HWND/Z-order state, NOT proof of physical screen rendering.
    public string GetDiagnosticState()
    {
        var window = _window;
        if (window is null) return "캘린더 창이 생성되지 않았습니다.";
        var hostAttached = !_standaloneWindowMode && _host.IsInteractiveSurfaceAttached(window);
        var retryState = _deferredPlacementTimer?.IsEnabled == true
            ? $"대기 중 ({_nextDeferredPlacementUtc.ToLocalTime():HH:mm:ss})"
            : _deferredPlacementAttempts >= DeferredPlacementDelays.Length ? "횟수 소진" : "없음";
        return $"Mode={(_standaloneWindowMode ? "Standalone (user selected)" : "Desktop band")}, "
            + $"WPF Visible={window.IsVisible}, Opacity={window.Opacity:F2}, "
            + $"Desktop band={hostAttached}, Bounds=({window.Left:F0},{window.Top:F0}) "
            + $"{window.Width:F0}x{window.Height:F0}, "
            + $"AutoRetrySuspended={_automaticPlacementSuppressed}, "
            + $"DeferredRetry={_deferredPlacementAttempts}/{DeferredPlacementDelays.Length}, Next={retryState}, "
            + $"Last error={(string.IsNullOrWhiteSpace(_lastPlacementProblem) ? "없음" : _lastPlacementProblem)}";
    }

    public bool TryResetPositionAndRepair()
    {
        if (_disposed) return false;
        if (_window is null) Start();
        if (_window is null) return false;
        _window.ResetPositionToPrimaryWorkArea();
        _window.EnsureWithinVisibleWorkArea("manual position reset");
        return TryRepairVisibleSurface();
    }

    public bool TryRepairVisibleSurface()
    {
        if (_disposed) return false;
        if (_standaloneWindowMode) return TryOpenStandaloneWindow();
        var now = DateTime.UtcNow;
        if (now - _lastManualRepairUtc < ManualRepairCooldown)
        {
            _lastPlacementProblem = "표시 복구 재시도는 15초 후에 가능합니다. (Explorer 반복 조작 방지)";
            return false;
        }
        _lastManualRepairUtc = now;
        try
        {
            // Read-only: never send undocumented 0x052C messages to Progman.
            _host.RefreshDesktopShellIdentity(out _);
            if (_window is null)
            {
                Start();
            }
            else
            {
                _window.ApplySizeFromSettings();
                _automaticPlacementSuppressed = false;
                EnsureAttachedAfterShellRebuild(force: true);
            }
            var attached = IsSurfaceVisible;
            if (!attached)
                DiagnosticsLog.Warn("Calendar manual repair stopped safely: " + _lastPlacementProblem);
            return attached;
        }
        catch (Exception ex)
        {
            _automaticPlacementSuppressed = true;
            _lastPlacementProblem = ex.Message;
            DiagnosticsLog.Error("Calendar manual desktop repair failed", ex);
            return false;
        }
    }

    /// <summary>
    /// A genuine Explorer desktop HWND was destroyed and replaced. Allow ONE
    /// attachment attempt for the new host, not a periodic Z-order loop.
    /// </summary>
    public void NotifyExplorerShellRebuilt()
    {
        if (_disposed || _standaloneWindowMode || _window is null) return;
        _automaticPlacementSuppressed = false;
        _window.EnsureWithinVisibleWorkArea("Explorer shell rebuilt");
        EnsureAttachedAfterShellRebuild(force: true);
    }

    public void EnsureAttachedAfterShellRebuild(bool force = false)
    {
        var window = _window;
        if (window is null || _disposed || _standaloneWindowMode || _attachmentAttemptInProgress) return;
        // No automatic restacking during editing, foreground activation, or
        // once a failed desktop placement has tripped the circuit breaker.
        if (window.IsEditing || (!force && _automaticPlacementSuppressed)) return;
        var now = DateTime.UtcNow;
        if (!force && now - _lastAttachmentAttemptUtc < TimeSpan.FromSeconds(10)) return;
        _lastAttachmentAttemptUtc = now;
        _attachmentAttemptInProgress = true;
        try
        {
            window.EnsureWithinVisibleWorkArea("placement attempt");
            // Critically: do NOT call Window.Show() before verifying placement.
            // In the failing v17 build Show/Hide/Activated feedback generated
            // dozens of Z-order repairs per second, flashing other applications.
            if (!_host.TryAttachInteractiveSurface(window, out var reason))
            {
                HideFailedSurface(window, reason, now);
                return;
            }

            if (!window.IsVisible)
            {
                window.Opacity = 0;
                window.Show(); // ShowActivated=false; remains fully transparent during placement.

                // WPF Show can move the HWND ABOVE ordinary applications after the
                // preliminary hidden-window placement. v21 only checked the position
                // here and immediately hid the calendar. Make ONE non-activating
                // corrective placement AFTER Show, while Opacity is still zero.
                // This runs only in the explicit startup/shell-rebuild/manual path;
                // no Activated/Deactivated/IsVisibleChanged callback may restack it.
                if (!_host.TryAttachInteractiveSurface(window, out var postShowReason))
                {
                    HideFailedSurface(window,
                        "Post-Show desktop placement failed: " + postShowReason, now);
                    return;
                }
            }

            window.Opacity = double.IsFinite(_settings.CalendarOpacity)
                ? Math.Clamp(_settings.CalendarOpacity, 0.2, 1.0) : 0.86;
            window.ApplyCalendarAppearance();
            _deferredPlacementTimer?.Stop();
            _nextDeferredPlacementUtc = DateTime.MinValue;
            _lastPlacementProblem = "";
            _automaticPlacementSuppressed = false;
            DiagnosticsLog.Info("Calendar desktop surface attached and visible");
        }
        catch (Exception ex)
        {
            HideFailedSurface(window, ex.Message, now);
        }
        finally { _attachmentAttemptInProgress = false; }
    }

    private void HideFailedSurface(CalendarWindow window, string reason, DateTime now)
    {
        // Fail closed without ever activating or restacking an unrelated window.
        window.Opacity = 0;
        if (window.IsVisible) window.Hide();
        _host.ForgetInteractiveSurface(window);
        _automaticPlacementSuppressed = true;
        ScheduleBoundedPlacementRetry(reason);
        if (reason != _lastPlacementProblem || now - _lastPlacementWarningUtc > TimeSpan.FromMinutes(2))
        {
            DiagnosticsLog.Warn("Calendar placement disabled for this session: " + reason);
            _lastPlacementWarningUtc = now;
        }
        _lastPlacementProblem = reason;
    }

    private void ScheduleBoundedPlacementRetry(string reason)
    {
        if (_disposed || !_settings.EnableCalendar || _window is null
            || _deferredPlacementTimer?.IsEnabled == true
            || _deferredPlacementAttempts >= DeferredPlacementDelays.Length)
            return;
        // Only retry a transient Windows Z-order failure. Other failures
        // (missing Explorer, invalid owner, unexpected exception) need manual
        // diagnosis rather than repeated shell manipulation.
        if (!reason.Contains("Cannot enter desktop Z-order band", StringComparison.Ordinal)
            && !reason.Contains("Post-Show desktop placement failed:", StringComparison.Ordinal))
            return;
        var delay = DeferredPlacementDelays[_deferredPlacementAttempts++];
        _nextDeferredPlacementUtc = DateTime.UtcNow + delay;
        _deferredPlacementTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _deferredPlacementTimer.Stop();
        _deferredPlacementTimer.Interval = delay;
        _deferredPlacementTimer.Tick -= OnDeferredPlacementRetry;
        _deferredPlacementTimer.Tick += OnDeferredPlacementRetry;
        _deferredPlacementTimer.Start();
        DiagnosticsLog.Info($"Calendar desktop placement: bounded hidden retry "
            + $"{_deferredPlacementAttempts}/{DeferredPlacementDelays.Length} scheduled in {delay.TotalSeconds:F0}s");
    }

    private void OnDeferredPlacementRetry(object? sender, EventArgs e)
    {
        _deferredPlacementTimer?.Stop();
        _nextDeferredPlacementUtc = DateTime.MinValue;
        if (_disposed || !_settings.EnableCalendar || _window is null) return;
        if (_window.IsVisible || _window.IsEditing) return;
        // Do not call Show() unless the invisible HWND is safely attached to
        // Explorer's desktop band. Even the retry runs without activation.
        DiagnosticsLog.Info($"Calendar desktop placement: bounded hidden retry "
            + $"{_deferredPlacementAttempts}/{DeferredPlacementDelays.Length} starting");
        EnsureAttachedAfterShellRebuild(force: true);
    }

    // Combined mode may poll for a hidden surface. Failed placement is latched:
    // the polling path must not rearrange Explorer/window Z-order again.
    public void RetryHiddenSurface()
    {
        if (_window is { IsVisible: false } && !_disposed && !_standaloneWindowMode && !_automaticPlacementSuppressed)
            EnsureAttachedAfterShellRebuild();
    }

    public async Task SynchronizeAsync()
    {
        if (_window is null) return;
        if (_window.IsGoogleConnecting)
        {
            _window.SetStatus("Google 계정 연결을 완료하거나 취소한 뒤 동기화하세요.");
            return;
        }
        if (_window.IsEditing) { _window.SetStatus("일정을 저장하거나 취소한 후 동기화하세요."); return; }
        if (_window.IsSynchronizing) return;
        _window.IsSynchronizing = true;
        _window.SetStatus("Google Calendar 동기화 중…");
        try
        {
            var result = await _google.SynchronizeAsync();
            if (_window is null || _disposed) return;
            _window.SetStatus(result);
            // Keep an open settings panel, editor, drag and scroll position intact.
            _window.RenderMonthWhenIdle();
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // A pending request may be cancelled while this module is being switched off.
        }
        catch (Exception ex)
        {
            // DispatcherTimer's async event callback must never propagate an exception
            // via async void and terminate the host CyanDeck application.
            DiagnosticsLog.Error("Calendar synchronization failed", ex);
            if (_window is not null && !_disposed)
                _window.SetStatus("Google Calendar 동기화 실패. 연결 상태를 확인하고 다시 시도하세요.");
        }
        finally
        {
            if (_window is not null) _window.IsSynchronizing = false;
            AfterSynchronizeAttempt();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shellScheduler?.Dispose();
        _deferredPlacementTimer?.Stop();
        if (_deferredPlacementTimer is not null)
            _deferredPlacementTimer.Tick -= OnDeferredPlacementRetry;
        _deferredPlacementTimer = null;
        _showDesktopRecovery?.Dispose();
        _showDesktopRecovery = null;
        _syncTimer?.Stop();
        _promptSyncTimer?.Stop();
        _holidayTimer?.Stop();
        _holidayCancellation.Cancel();
        _shellScheduler = null;
        _syncTimer = null;
        _holidayTimer = null;
        if (_window is not null)
        {
            _host.ForgetInteractiveSurface(_window);
            _window.AllowFinalClose();
            _window.Close();
            _window = null;
        }
        _google.Dispose();
        _holidayFeeds.Dispose();
    }
}
