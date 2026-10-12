using System;
using System.Windows.Threading;

namespace DesktopDeck.Services;

/// <summary>
/// Owns a single UI-thread shell recovery heartbeat. No Z-order operations are
/// performed here: the module/controller decides when and what is safe to repair.
/// Detaching Tick on dispose prevents obsolete callbacks after mode changes.
/// </summary>
internal sealed class DesktopShellRecoveryScheduler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _onTick;
    private readonly string _label;
    private bool _running;
    private bool _disposed;
    private bool _executing;

    public DesktopShellRecoveryScheduler(Dispatcher dispatcher, TimeSpan interval, Action onTick, string label)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(onTick);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _onTick = onTick;
        _label = label;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = interval
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;
        _running = true;
        _timer.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || !_running || _executing) return;
        _executing = true;
        try { _onTick(); }
        catch (Exception ex) { DiagnosticsLog.Error(_label + " tick failed", ex); }
        finally { _executing = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _timer.Tick -= OnTick;
    }
}
