using System;
using System.Windows.Threading;

namespace CyanDeck.Services;

/// <summary>
/// Single-shot, debounced desktop reconciliation on the WPF owner dispatcher.
/// The controller owns the action; this component owns only timer lifecycle.
/// </summary>
internal sealed class DesktopReconcileScheduler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _reconcile;
    private bool _disposed;

    public DesktopReconcileScheduler(Dispatcher dispatcher, Action reconcile)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(reconcile);
        _reconcile = reconcile;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += OnTick;
    }

    public void Request(int delayMs = 350)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 100, 3000));
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed) return;
        try { _reconcile(); }
        catch (Exception ex) { DiagnosticsLog.Error("Desktop reconcile failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
