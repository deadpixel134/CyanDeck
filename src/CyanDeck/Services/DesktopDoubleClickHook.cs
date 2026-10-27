using System.Runtime.InteropServices;
using System.Windows.Threading;
using CyanDeck.Interop;

namespace CyanDeck.Services;

public sealed class DesktopDoubleClickHook : IDisposable, ILowLevelMouseHookClient
{
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private readonly Func<bool>? _isEnabled;
    // v8.0.43: the hook lives on CyanDeck's dedicated input thread (see LowLevelMouseHookThread)
    // so a busy UI thread can no longer stall the system mouse or get the hook removed.
    private LowLevelMouseHookThread? _hookThread;
    private IntPtr _hook;
    private int _hookFaulted;
    private bool _started;
    private volatile bool _disposed;
    private long _callbackCount;
    private long _lastClick;
    private NativeMethods.POINT _lastPoint;

    public event Action? DoubleClicked;

    // isEnabled is evaluated on the hook thread; it must only read plain settings values.
    public DesktopDoubleClickHook(Dispatcher dispatcher, Func<bool>? isEnabled = null)
    {
        _dispatcher = dispatcher;
        _isEnabled = isEnabled;
        _proc = Callback;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        if (LowLevelMouseHookThread.TryGetShared(out var thread))
        {
            _hookThread = thread;
            if (!thread.Invoke(InstallHook))
                DiagnosticsLog.Warn("Desktop double-click hook install was delayed; the hook watchdog retries it.");
            thread.Register(this);
            return;
        }

        // Fallback only when the dedicated thread cannot start: previous UI-thread hook.
        InstallHook();
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero || _disposed) return;
        _hook = NativeMethods.SetWindowsHookExMouse(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) DiagnosticsLog.Warn("Desktop double-click mouse hook could not be installed.");
    }

    private void UninstallHook()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _callbackCount++;
        if (System.Threading.Volatile.Read(ref _hookFaulted) != 0)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
        if (nCode >= 0 && wParam.ToInt32() == NativeMethods.WM_LBUTTONDOWN)
        {
            // Skip the desktop hit test entirely while the double-click command is off.
            if (_isEnabled is not null && !_isEnabled())
            {
                _lastClick = 0;
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
            }
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            if (IsBlankDesktop(data.pt))
            {
                var now = Environment.TickCount64;
                var dx = Math.Abs(data.pt.X - _lastPoint.X);
                var dy = Math.Abs(data.pt.Y - _lastPoint.Y);
                if (now - _lastClick <= 480 && dx <= 5 && dy <= 5)
                {
                    _lastClick = 0;
                    if (DoubleClicked is { } handler) _dispatcher.BeginInvoke(handler);
                }
                else
                {
                    _lastClick = now;
                    _lastPoint = data.pt;
                }
            }
            else
            {
                _lastClick = 0;
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        catch (Exception)
        {
            // Never swallow Windows mouse events because of a CyanDeck error.
            // Uninstall the faulty hook asynchronously; do not block the native callback.
            if (System.Threading.Interlocked.Exchange(ref _hookFaulted, 1) == 0)
            {
                try { PostDisposal(() => { Dispose(); DiagnosticsLog.Warn("Desktop mouse hook disabled after callback error; input passed to Windows."); }); }
                catch { /* CallNextHookEx below still fails open. */ }
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    private void PostDisposal(Action action)
    {
        if (_hookThread is { } thread) thread.Post(action);
        else _dispatcher.BeginInvoke(action);
    }

    private static bool IsBlankDesktop(NativeMethods.POINT point)
    {
        if (!DesktopShellHandleCache.TryLocate(out var parent, out var defView, out var listView))
            return false;

        var hwnd = NativeMethods.WindowFromPoint(point);
        for (var i = 0; i < 7 && hwnd != IntPtr.Zero; i++)
        {
            // Preserve the existing behavior: the native FolderView itself is never considered
            // blank here, while the canonical DefView/WorkerW background is. The important part
            // is that similarly named controls in third-party file managers are ignored.
            if (hwnd == listView) return false;
            if (hwnd == defView || hwnd == parent) return true;
            hwnd = NativeMethods.GetParent(hwnd);
        }
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        var thread = _hookThread;
        if (thread is not null && !thread.IsCurrentThread)
        {
            thread.Unregister(this);
            if (!thread.Invoke(UninstallHook))
                DiagnosticsLog.Warn("Desktop double-click hook could not be removed on its input thread.");
            return;
        }
        thread?.Unregister(this);
        UninstallHook();
    }

    string ILowLevelMouseHookClient.HookName => "desktop-double-click";
    bool ILowLevelMouseHookClient.WantsHook => _started && !_disposed && System.Threading.Volatile.Read(ref _hookFaulted) == 0;
    bool ILowLevelMouseHookClient.HasHookHandle => _hook != IntPtr.Zero;
    long ILowLevelMouseHookClient.CallbackCount => _callbackCount;

    void ILowLevelMouseHookClient.ReinstallHookOnHookThread()
    {
        UninstallHook();
        _lastClick = 0;
        InstallHook();
    }
}
