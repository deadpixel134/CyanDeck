using System;
using System.Windows;

namespace DesktopDeck.Peek;

public sealed class WpfPeekHotCornerHost : IDisposable
{
    private readonly Func<PeekHotCornerOptions> _getOptions;
    private readonly WpfPeekHotCornerWindow _window;
    private bool _disposed;

    public WpfPeekHotCornerHost(
        Func<PeekHotCornerOptions> getOptions,
        Action togglePeek,
        Action toggleDesktopDisplay,
        Action<Exception>? reportError = null)
    {
        _getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
        _window = new WpfPeekHotCornerWindow(
            new PeekHotCornerActionRouter(togglePeek, toggleDesktopDisplay, reportError));
    }

    public bool Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var options = _getOptions() ?? new PeekHotCornerOptions();
        _window.UpdateOptions(options);

        if (!options.Enabled || !options.HasAnyAction())
        {
            HideWindow();
            return false;
        }

        if (!WindowsPrimaryTaskbarReader.TryRead(out var taskbar) ||
            !taskbar.IsValid ||
            taskbar.IsAutoHide)
        {
            HideWindow();
            return false;
        }

        var bounds = TaskbarHotCornerGeometry.Compute(taskbar, options);
        if (!bounds.IsValid)
        {
            HideWindow();
            return false;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.SetPhysicalBounds(bounds);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Close();
    }

    private void HideWindow()
    {
        if (_window.IsVisible)
        {
            _window.Hide();
        }
    }
}
