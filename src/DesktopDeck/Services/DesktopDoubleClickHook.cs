using System.Runtime.InteropServices;
using System.Windows.Threading;
using DesktopDeck.Interop;

namespace DesktopDeck.Services;

public sealed class DesktopDoubleClickHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private int _hookFaulted;
    private long _lastClick;
    private NativeMethods.POINT _lastPoint;

    public event Action? DoubleClicked;

    public DesktopDoubleClickHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWindowsHookExMouse(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (System.Threading.Volatile.Read(ref _hookFaulted) != 0)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
        if (nCode >= 0 && wParam.ToInt32() == NativeMethods.WM_LBUTTONDOWN)
        {
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
            // Never swallow Windows mouse events because of a DesktopDeck error.
            // Uninstall the faulty hook asynchronously; do not block the native callback.
            if (System.Threading.Interlocked.Exchange(ref _hookFaulted, 1) == 0)
            {
                try { _dispatcher.BeginInvoke(new Action(() => { Dispose(); DiagnosticsLog.Warn("Desktop mouse hook disabled after callback error; input passed to Windows."); })); }
                catch { /* CallNextHookEx below still fails open. */ }
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    private static bool IsBlankDesktop(NativeMethods.POINT point)
    {
        if (!DesktopHost.TryLocateDesktopShell(out var parent, out var defView, out var listView))
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
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
