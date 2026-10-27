using CyanDeck.Views;

namespace CyanDeck.Services;

/// <summary>
/// Group-item and system-icon helpers: keeping the Recycle Bin empty/full icon current and
/// telling the loose desktop icon host where group windows are for automatic placement.
/// </summary>
public sealed partial class AppController
{
    private RecycleBinStateMonitor? _recycleBinMonitor;

    private void StartRecycleBinStateMonitor()
    {
        if (_safeMode || _disposed || _recycleBinMonitor is not null) return;
        try
        {
            _recycleBinMonitor = new RecycleBinStateMonitor(_dispatcher, OnRecycleBinStateChanged);
            _recycleBinMonitor.Start();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Recycle Bin state monitor failed to start", ex);
            _recycleBinMonitor?.Dispose();
            _recycleBinMonitor = null;
        }
    }

    private void StopRecycleBinStateMonitor()
    {
        _recycleBinMonitor?.Dispose();
        _recycleBinMonitor = null;
    }

    /// <summary>
    /// The Recycle Bin switched between empty and full. Reload only the Recycle Bin
    /// icon: the standalone desktop surrogate and any group that currently shows it.
    /// </summary>
    private void OnRecycleBinStateChanged()
    {
        if (_disposed) return;
        _icons.Invalidate(DesktopSystemItems.RecycleBinShellPath);
        _looseDesktopHost?.RefreshIcon(DesktopSystemItems.RecycleBinShellPath);
        foreach (var window in _windows.ToArray())
        {
            try { window.RefreshRecycleBinIcon(); }
            catch (Exception ex) { DiagnosticsLog.Error("Group Recycle Bin icon refresh failed", ex); }
        }
    }

    /// <summary>
    /// Physical-pixel bounds of the group windows on the current page. Automatic loose
    /// icon placement avoids these rectangles so a new icon never lands under a group.
    /// </summary>
    private IReadOnlyList<System.Drawing.Rectangle> GetLooseAutoPlacementObstacles()
    {
        var obstacles = new List<System.Drawing.Rectangle>();
        foreach (FenceWindow window in _windows)
        {
            try
            {
                var fence = window.Model;
                if (!fence.ShowOnAllPages && fence.PageIndex != _state.CurrentPage) continue;
                if (window.TryGetPhysicalBounds(out var bounds) && bounds.Width > 0 && bounds.Height > 0)
                    obstacles.Add(bounds);
            }
            catch { }
        }
        return obstacles;
    }
}
