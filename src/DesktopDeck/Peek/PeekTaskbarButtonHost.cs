using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DesktopDeck.Peek;

public sealed class PeekTaskbarButtonHost : IDisposable
{
    private readonly Action _togglePeek;
    private WpfPeekTaskbarButtonWindow? _window;
    private PeekTaskbarButtonOptions _options = new();
    private bool _disposed;

    public PeekTaskbarButtonHost(Action togglePeek)
    {
        _togglePeek = togglePeek ?? throw new ArgumentNullException(nameof(togglePeek));
    }

    public void Apply(PeekTaskbarButtonOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = (options ?? new PeekTaskbarButtonOptions()).Normalize();

        if (!normalized.Enabled)
        {
            _options = normalized;
            CloseWindow();
            return;
        }

        if (_window is not null && normalized == _options)
        {
            _window.EnsureTaskbarSurfaceVisible();
            return;
        }

        _options = normalized;
        RecreateWindow();
    }

    public void EnsureVisible()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.Enabled) return;
        if (_window is null)
        {
            RecreateWindow();
            return;
        }
        _window.EnsureTaskbarSurfaceVisible();
    }

    public void RefreshIcon()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_options.Enabled)
        {
            RecreateWindow();
        }
    }

    private void RecreateWindow()
    {
        CloseWindow();

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            return;
        }

        var icon = PeekTaskbarButtonIconResolver.Resolve(executablePath, _options.CustomIconPath);
        var window = new WpfPeekTaskbarButtonWindow(
            executablePath,
            icon,
            _options.ActivationDebounceMilliseconds,
            _togglePeek);

        _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window)) _window = null;
        };
        window.Show();
    }

    private void CloseWindow()
    {
        var old = _window;
        _window = null;
        old?.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseWindow();
        GC.SuppressFinalize(this);
    }
}
