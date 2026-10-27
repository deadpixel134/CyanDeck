using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using CyanDeck.Interop;

namespace CyanDeck.Services;

/// <summary>
/// Keeps the Recycle Bin icon's empty/full artwork current. The icon itself is chosen by
/// <see cref="IconService"/> when it is loaded, so this monitor only detects a change of the
/// empty/full state and asks the owner to reload the Recycle Bin icon.
///
/// Triggers: Windows Shell change notifications for the Recycle Bin namespace object, an
/// explicit <see cref="RequestCheck"/> after CyanDeck's own recycle/menu actions, and a slow
/// safety poll. Every trigger is throttled into one background SHQueryRecycleBin call so
/// slow or numerous drives never block the UI thread.
/// </summary>
internal sealed class RecycleBinStateMonitor : IDisposable
{
    internal static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(700);
    // Shell notifications can stop after an Explorer restart and are not raised by every
    // cleaner (Storage Sense etc.), so the cheap background check still runs every minute.
    internal static readonly TimeSpan PollIntervalWithShellNotifications = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan PollIntervalWithoutShellNotifications = TimeSpan.FromSeconds(45);

    private const int WM_RECYCLE_BIN_NOTIFY = 0x0400 + 0x2C1;
    private const int CSIDL_BITBUCKET = 0x000A;
    private const int SHCNRF_ShellLevel = 0x0002;
    private const int SHCNRF_NewDelivery = 0x8000;
    private const int SHCNE_ALLEVENTS = 0x7FFFFFFF;

    private static RecycleBinStateMonitor? _current;

    private readonly Dispatcher _dispatcher;
    private readonly Action _onStateChanged;
    private readonly DispatcherTimer _checkTimer;
    private readonly DispatcherTimer _pollTimer;
    private HwndSource? _notifySource;
    private HwndSourceHook? _notifyHook;
    private IntPtr _recycleBinPidl;
    private uint _notifyRegistration;
    private bool? _lastHasItems;
    private bool _checkRunning;
    private bool _checkQueued;
    private bool _disposed;

    internal RecycleBinStateMonitor(Dispatcher dispatcher, Action onStateChanged)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onStateChanged = onStateChanged ?? throw new ArgumentNullException(nameof(onStateChanged));
        _checkTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = CheckDelay };
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            CheckNow();
        };
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = PollIntervalWithoutShellNotifications };
        _pollTimer.Tick += (_, _) => RequestCheckSoon();
    }

    /// <summary>Must be called on the owner's dispatcher thread.</summary>
    internal void Start()
    {
        if (_disposed) return;
        _current = this;
        var notifications = TryRegisterShellNotifications();
        _pollTimer.Interval = notifications ? PollIntervalWithShellNotifications : PollIntervalWithoutShellNotifications;
        _pollTimer.Start();
        // The first result is only a baseline: icons loaded at startup already match it.
        RequestCheckSoon();
    }

    /// <summary>
    /// Thread-safe hint that the Recycle Bin may have changed (for example after CyanDeck
    /// recycled a desktop file or after the Windows Recycle Bin menu was used).
    /// </summary>
    internal static void RequestCheck() => _current?.RequestCheckSoon();

    private void RequestCheckSoon()
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            try { _dispatcher.BeginInvoke(new Action(RequestCheckSoon), DispatcherPriority.Background); }
            catch { }
            return;
        }
        // Throttle: a burst of notifications arms the short timer once.
        if (!_checkTimer.IsEnabled) _checkTimer.Start();
    }

    private async void CheckNow()
    {
        if (_disposed) return;
        if (_checkRunning)
        {
            _checkQueued = true;
            return;
        }
        _checkRunning = true;
        try
        {
            var hasItems = await Task.Run(() => IconService.QueryRecycleBinHasItems());
            if (_disposed || hasItems is null) return;
            var previous = _lastHasItems;
            _lastHasItems = hasItems;
            if (previous is not null && previous.Value != hasItems.Value)
            {
                DiagnosticsLog.Info(hasItems.Value ? "Recycle Bin became non-empty; refreshing icon" : "Recycle Bin became empty; refreshing icon");
                _onStateChanged();
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Recycle Bin state check failed", ex);
        }
        finally
        {
            _checkRunning = false;
            if (_checkQueued && !_disposed)
            {
                _checkQueued = false;
                RequestCheckSoon();
            }
        }
    }

    private bool TryRegisterShellNotifications()
    {
        try
        {
            if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_BITBUCKET, out var pidl) != 0 || pidl == IntPtr.Zero)
                return false;
            _recycleBinPidl = pidl;

            var parameters = new HwndSourceParameters("CyanDeck.RecycleBinStateMonitor")
            {
                Width = 0,
                Height = 0,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = 0,
                ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
            };
            _notifySource = new HwndSource(parameters);
            _notifyHook = NotifyHook;
            _notifySource.AddHook(_notifyHook);

            var entries = new[] { new SHChangeNotifyEntry { pidl = _recycleBinPidl, fRecursive = true } };
            _notifyRegistration = SHChangeNotifyRegister(
                _notifySource.Handle,
                SHCNRF_ShellLevel | SHCNRF_NewDelivery,
                SHCNE_ALLEVENTS,
                WM_RECYCLE_BIN_NOTIFY,
                entries.Length,
                entries);
            if (_notifyRegistration != 0) return true;
            DiagnosticsLog.Warn("Recycle Bin shell change notification registration failed; using periodic checks");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Recycle Bin shell change notification setup failed", ex);
        }
        ReleaseShellNotifications();
        return false;
    }

    private IntPtr NotifyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_RECYCLE_BIN_NOTIFY) return IntPtr.Zero;
        handled = true;
        try
        {
            // New-delivery notifications are shared memory; map and release it even though
            // only the fact that "something changed" is used.
            var lockHandle = SHChangeNotification_Lock(wParam, unchecked((uint)lParam.ToInt64()), out _, out _);
            if (lockHandle != IntPtr.Zero) SHChangeNotification_Unlock(lockHandle);
        }
        catch { }
        RequestCheckSoon();
        return IntPtr.Zero;
    }

    private void ReleaseShellNotifications()
    {
        if (_notifyRegistration != 0)
        {
            try { SHChangeNotifyDeregister(_notifyRegistration); } catch { }
            _notifyRegistration = 0;
        }
        if (_notifySource is not null)
        {
            try
            {
                if (_notifyHook is not null) _notifySource.RemoveHook(_notifyHook);
                _notifySource.Dispose();
            }
            catch { }
            _notifySource = null;
            _notifyHook = null;
        }
        if (_recycleBinPidl != IntPtr.Zero)
        {
            try { ILFree(_recycleBinPidl); } catch { }
            _recycleBinPidl = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(_current, this)) _current = null;
        _checkTimer.Stop();
        _pollTimer.Stop();
        ReleaseShellNotifications();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHChangeNotifyEntry
    {
        public IntPtr pidl;
        [MarshalAs(UnmanagedType.Bool)] public bool fRecursive;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(IntPtr hwndOwner, int nFolder, out IntPtr ppidl);

    [DllImport("shell32.dll")]
    private static extern uint SHChangeNotifyRegister(IntPtr hwnd, int fSources, int fEvents, int wMsg, int cEntries,
        [In] SHChangeNotifyEntry[] pshcne);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotifyDeregister(uint ulID);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHChangeNotification_Lock(IntPtr hChange, uint dwProcId, out IntPtr pppidl, out int plEvent);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotification_Unlock(IntPtr hLock);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
