using System;
using System.IO;
using System.Threading;

namespace CyanDeck.ManagedDeployment;

public sealed class ManagedPolicyFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly Action _reloadRequested;
    private readonly Action<string>? _diagnostic;
    private int _disposed;

    public ManagedPolicyFileWatcher(
        string policyPath,
        Action reloadRequested,
        Action<string>? diagnostic = null,
        TimeSpan? debounce = null)
    {
        if (!ManagedPolicyReader.TryClassifyPath(policyPath, out _, out var error))
            throw new ArgumentException(error, nameof(policyPath));

        _reloadRequested = reloadRequested ?? throw new ArgumentNullException(nameof(reloadRequested));
        _diagnostic = diagnostic;
        DebounceDelay = debounce ?? TimeSpan.FromMilliseconds(350);
        if (DebounceDelay < TimeSpan.FromMilliseconds(50) || DebounceDelay > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(debounce));

        var directory = Path.GetDirectoryName(policyPath) ?? throw new ArgumentException("Policy directory is unavailable.", nameof(policyPath));
        var fileName = Path.GetFileName(policyPath);
        _debounce = new Timer(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, fileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = false
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    public TimeSpan DebounceDelay { get; }

    private void OnChanged(object sender, FileSystemEventArgs e) => Schedule();
    private void OnRenamed(object sender, RenamedEventArgs e) => Schedule();

    private void OnError(object sender, ErrorEventArgs e)
    {
        _diagnostic?.Invoke("Managed deployment watcher reported an error; active policy remains unchanged. Use manual reload if needed.");
    }

    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnDebounce(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try { _reloadRequested(); }
        catch (Exception ex) { _diagnostic?.Invoke($"Managed deployment reload callback failed: {ex.GetType().Name}"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
