using System;
using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace CyanDeck.Services;

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
    // v8.0.43: dimmed icon shown while the desktop is quick-hidden, and the action of
    // the most recent balloon (null when that balloon has no click action).
    private Icon? _hiddenStateIcon;
    private bool _hiddenStateShown;
    private Action? _balloonClick;
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
            try
            {
                populateMenu(_menu);
                // Dark look, also for submenus built just now. A theme failure must never
                // keep the tray menu from opening.
                try { DeckToolStripRenderer.Apply(_menu); }
                catch (Exception themeEx) { DiagnosticsLog.Error("Tray menu theme failed; using the plain menu", themeEx); }
            }
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
        _notify.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonClick;
            _balloonClick = null;
            if (_disposed || action is null) return;
            try { action(); }
            catch (Exception ex) { DiagnosticsLog.Error("Tray notification action failed", ex); }
        };
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
        _balloonClick = null;
        if (!_disposed) _notify.ShowBalloonTip(timeoutMs, title, message, icon);
    }

    // Same notification, plus an action when the user clicks it (e.g. open Settings).
    public void ShowBalloonTip(int timeoutMs, string title, string message, Forms.ToolTipIcon icon, Action onClick)
    {
        if (_disposed) return;
        _balloonClick = onClick;
        _notify.ShowBalloonTip(timeoutMs, title, message, icon);
    }

    /// <summary>
    /// Shows a dimmed copy of the tray icon while the desktop is quick-hidden so the state
    /// is visible without hovering. A drawing failure keeps the normal icon.
    /// </summary>
    public void SetQuickHiddenAppearance(bool hidden)
    {
        if (_disposed || hidden == _hiddenStateShown) return;
        try
        {
            if (hidden)
            {
                _hiddenStateIcon ??= CreateDimmedIcon(_ownedIcon ?? SystemIcons.Application);
                if (_hiddenStateIcon is null) return;
                _notify.Icon = _hiddenStateIcon;
            }
            else
            {
                _notify.Icon = _ownedIcon ?? SystemIcons.Application;
            }
            _hiddenStateShown = hidden;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn($"Tray quick-hide icon could not be changed: {ex.Message}");
        }
    }

    private static Icon? CreateDimmedIcon(Icon source)
    {
        var size = Forms.SystemInformation.SmallIconSize;
        using var sized = new Icon(source, size);
        using var original = sized.ToBitmap();
        using var dimmed = new Bitmap(original.Width, original.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(dimmed))
        {
            // Grayscale at 45% opacity: clearly "inactive" in both light and dark taskbars.
            var matrix = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0f, 0f },
                new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                new[] { 0f, 0f, 0f, 0.45f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            });
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(matrix);
            graphics.DrawImage(original, new Rectangle(0, 0, original.Width, original.Height),
                0, 0, original.Width, original.Height, GraphicsUnit.Pixel, attributes);
        }

        var handle = dimmed.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            // Clone copies the native icon, so the temporary HICON can be destroyed now.
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

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
        _hiddenStateIcon?.Dispose();
        _balloonClick = null;
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
