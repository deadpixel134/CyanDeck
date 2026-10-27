using System.Globalization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CyanDeck.Calendar;

/// <summary>
/// Wakes the calendar when the local date may have changed: one dispatcher timer armed for
/// the next local midnight (recomputed every time, so DST and time-zone changes are honoured),
/// plus system clock/time-zone changes and resume from sleep. The check callback returns false
/// while the calendar is busy (editing, dragging, a menu or the settings panel); the check is
/// then retried shortly instead of interrupting the user. Presentation only: it never saves.
/// </summary>
internal sealed class CalendarDayRolloverWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _checkLocalDate;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public CalendarDayRolloverWatcher(Dispatcher dispatcher, Func<bool> checkLocalDate)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _checkLocalDate = checkLocalDate ?? throw new ArgumentNullException(nameof(checkLocalDate));
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _timer.Tick += OnTimerTick;
        SystemEvents.TimeChanged += OnSystemTimeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Arm(CalendarTodayRefreshPolicy.DelayUntilNextCheck(DateTime.Now));
    }

    private void OnTimerTick(object? sender, EventArgs e) => RunCheck();

    // SystemEvents may raise on its own thread; always continue on the calendar's dispatcher.
    private void OnSystemTimeChanged(object? sender, EventArgs e)
        => QueueCheck(clearTimeZoneCache: true);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) QueueCheck(clearTimeZoneCache: false);
    }

    private void QueueCheck(bool clearTimeZoneCache)
    {
        if (_disposed) return;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed) return;
                if (clearTimeZoneCache)
                {
                    // DateTime.Now caches the local time zone; a zone change must be re-read.
                    TimeZoneInfo.ClearCachedData();
                    CultureInfo.CurrentCulture.ClearCachedData();
                }
                RunCheck();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void RunCheck()
    {
        if (_disposed) return;
        _timer.Stop();
        bool handled;
        try { handled = _checkLocalDate(); }
        catch (Exception ex)
        {
            CyanDeck.Services.DiagnosticsLog.Warn("Calendar today refresh failed: " + ex.Message);
            handled = true;
        }
        Arm(handled
            ? CalendarTodayRefreshPolicy.DelayUntilNextCheck(DateTime.Now)
            : CalendarTodayRefreshPolicy.BusyRetryInterval);
    }

    private void Arm(TimeSpan delay)
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
