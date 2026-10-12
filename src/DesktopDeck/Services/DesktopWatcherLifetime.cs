using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopDeck.Services;

/// <summary>
/// Owns desktop directory watchers. A root change is committed only after all
/// replacement watchers have been created and enabled successfully. A failure
/// leaves the previous live watcher set and root identity untouched.
/// The factory must return a subscribed, INACTIVE FileSystemWatcher.
/// </summary>
internal sealed class DesktopWatcherLifetime : IDisposable
{
    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _disposed;

    public int RootCount => _roots.Count;
    public string[] GetRoots() => _roots.ToArray();
    public bool Matches(IEnumerable<string> roots) => _roots.SetEquals(roots);

    /// <summary>
    /// Called on the UI/owner thread, including all calls to Owns().
    /// Queued callbacks from superseded watchers must check Owns() on that thread.
    /// </summary>
    public bool Owns(FileSystemWatcher? watcher) => !_disposed && watcher is not null && _watchers.Contains(watcher);

    public bool Rebind(IEnumerable<string> roots, Func<string, FileSystemWatcher?> create)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(create);
        var distinct = roots.Where(static root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var staged = new List<FileSystemWatcher>();
        var committed = false;
        try
        {
            foreach (var root in distinct)
            {
                var watcher = create(root);
                if (watcher is null)
                {
                    DiagnosticsLog.Warn($"Desktop watcher rebind rejected: could not create watcher for {root}");
                    return false;
                }
                if (_watchers.Contains(watcher) || staged.Contains(watcher))
                    throw new InvalidOperationException("Desktop watcher factory returned an already-owned watcher");
                staged.Add(watcher);
                if (watcher.EnableRaisingEvents)
                    throw new InvalidOperationException("Desktop watcher factory returned an already-active watcher");
            }
            foreach (var watcher in staged) watcher.EnableRaisingEvents = true;

            // No operation that can open a directory or enable a watcher occurs after
            // this commit boundary. Old callbacks will be ignored by Owns().
            var previous = _watchers.ToArray();
            _watchers.Clear();
            _watchers.AddRange(staged);
            _roots.Clear();
            foreach (var root in distinct) _roots.Add(root);
            committed = true;
            DisposeWatchers(previous);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Desktop watcher rebind failed; previous watcher set retained", ex);
            return false;
        }
        finally
        {
            if (!committed) DisposeWatchers(staged);
        }
    }

    private static void DisposeWatchers(IEnumerable<FileSystemWatcher> watchers)
    {
        foreach (var watcher in watchers)
        {
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
            catch (Exception ex) { DiagnosticsLog.Error("Desktop watcher disposal failed", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeWatchers(_watchers);
        _watchers.Clear();
        _roots.Clear();
    }
}
