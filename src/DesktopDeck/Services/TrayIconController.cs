using System;
using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace DesktopDeck.Services;

/// <summary>
/// Owns the native notification icon, its *persistent* menu, and its lifetime.
/// Command composition stays in AppController; this class knows nothing about
/// fence windows, calendar preferences, or the current feature combination.
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notify;
    private readonly Icon? _ownedIcon;
    private bool _disposed;

    public bool IsMenuVisible { get; private set; }
    public event Action? NativeMenuClosed;

    public TrayIconController(Action<Forms.ContextMenuStrip> populateMenu, Action onDoubleClick, Action<bool> onMenuVisibilityChanged)
    {
        ArgumentNullException.ThrowIfNull(populateMenu);
        ArgumentNullException.ThrowIfNull(onDoubleClick);
        ArgumentNullException.ThrowIfNull(onMenuVisibilityChanged);

        _menu = new Forms.ContextMenuStrip();
        _menu.Opening += (_, e) =>
        {
            if (_disposed) { e.Cancel = true; return; }
            try { populateMenu(_menu); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Failed to populate tray menu", ex);
                e.Cancel = true;
            }
        };
        _menu.Opened += (_, _) =>
        {
            IsMenuVisible = true;
            onMenuVisibilityChanged(true);
        };
        _menu.Closed += (_, _) =>
        {
            IsMenuVisible = false;
            onMenuVisibilityChanged(false);
            NativeMenuClosed?.Invoke();
        };

        _ownedIcon = LoadApplicationIcon();
        _notify = new Forms.NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "CyanDeck",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => { if (!_disposed) onDoubleClick(); };
    }

    // A WPF modal dialog must never be opened while the native menu owns mouse capture.
    public void CloseMenu()
    {
        if (!_disposed && _menu.Visible) _menu.Close();
    }

    public void SetText(string value)
    {
        if (!_disposed) _notify.Text = value;
    }

    public void ShowBalloonTip(int timeoutMs, string title, string message, Forms.ToolTipIcon icon)
    {
        if (!_disposed) _notify.ShowBalloonTip(timeoutMs, title, message, icon);
    }

    public void Hide()
    {
        if (!_disposed) _notify.Visible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }

    private static Icon? LoadApplicationIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
                return Icon.ExtractAssociatedIcon(executablePath);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn($"Failed to load application icon for the tray: {ex.Message}");
        }
        return null;
    }
}
