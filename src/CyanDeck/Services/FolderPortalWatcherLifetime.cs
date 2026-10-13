using System;
using System.IO;

namespace CyanDeck.Services;

/// <summary>
/// Owns one Folder Portal watcher. FileSystemWatcher invokes handlers on worker
/// threads; callbacks are marshalled to the caller's UI scheduler, and an epoch
/// rejects notifications queued by a watcher replaced or disposed meanwhile.
/// </summary>
internal sealed class FolderPortalWatcherLifetime : IDisposable
{
    private FileSystemWatcher? _watcher;
    private string? _path;
    private long _epoch;
    private bool _disposed;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private string? _lastFailedPath;

    public string? CurrentPath => _path;

    public bool Bind(string path, Action<Action> postToOwner,
        Action<string?, string?> onChanged, Action<Exception> onError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(postToOwner);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(onError);
        if (_watcher is not null && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return true;
        if (DateTime.UtcNow < _nextRetryUtc
            && string.Equals(_lastFailedPath, path, StringComparison.OrdinalIgnoreCase)) return false;

        FileSystemWatcher? candidate = null;
        var nextEpoch = _epoch + 1;
        void Post(Action action)
        {
            try
            {
                postToOwner(() =>
                {
                    if (_disposed || nextEpoch != _epoch) return;
                    action();
                });
            }
            catch (Exception ex) { DiagnosticsLog.Error("Folder Portal UI dispatch failed", ex); }
        }
        try
        {
            candidate = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 32 * 1024
            };
            candidate.Created += (_, e) => Post(() => onChanged(e.FullPath, null));
            candidate.Deleted += (_, e) => Post(() => onChanged(e.FullPath, null));
            candidate.Changed += (_, e) => Post(() => onChanged(e.FullPath, null));
            candidate.Renamed += (_, e) => Post(() => onChanged(e.FullPath, e.OldFullPath));
            candidate.Error += (_, e) => Post(() =>
            {
                // Dropped notifications require a full folder refresh.
                var error = e.GetException();
                Clear();
                _lastFailedPath = path;
                _nextRetryUtc = DateTime.UtcNow.AddSeconds(3);
                onError(error);
            });
            candidate.EnableRaisingEvents = true;

            var previous = _watcher;
            _watcher = candidate;
            _path = path;
            _epoch = nextEpoch;
            _nextRetryUtc = DateTime.MinValue;
            _lastFailedPath = null;
            candidate = null;
            StopAndDispose(previous);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error($"Could not watch Folder Portal: {path}", ex);
            _lastFailedPath = path;
            _nextRetryUtc = DateTime.UtcNow.AddSeconds(3);
            return false;
        }
        finally { StopAndDispose(candidate); }
    }

    public void Clear()
    {
        if (_disposed) return;
        _epoch++;
        var previous = _watcher;
        _watcher = null;
        _path = null;
        StopAndDispose(previous);
    }

    private static void StopAndDispose(FileSystemWatcher? watcher)
    {
        if (watcher is null) return;
        try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
        catch (Exception ex) { DiagnosticsLog.Error("Folder Portal watcher disposal failed", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
    }
}
