using System.Runtime.InteropServices;

namespace DesktopDeck.Appearance;

/// <summary>
/// One process-wide, event-driven foreground-window source for Chameleon activation.
/// Uses the documented WinEvent API out-of-context; it never injects code into another process.
/// Create/start this service on DesktopDeck's WPF UI thread so the thread already has a message loop.
/// </summary>
internal sealed class WindowsForegroundEventSource : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;

    private readonly WinEventProc _callback;
    private IntPtr _hook;
    private bool _disposed;

    internal WindowsForegroundEventSource()
    {
        // Keep a strong delegate reference for the full hook lifetime.
        _callback = OnWinEvent;
    }

    internal event Action<IntPtr>? ForegroundChanged;

    internal IntPtr CurrentForegroundWindow => GetForegroundWindow();

    internal bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != IntPtr.Zero)
        {
            return true;
        }

        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);

        if (_hook == IntPtr.Zero)
        {
            return false;
        }

        Publish(CurrentForegroundWindow);
        return true;
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint eventTimeMilliseconds)
    {
        if (_disposed || hWinEventHook != _hook || eventType != EventSystemForeground)
        {
            return;
        }

        Publish(hwnd);
    }

    private void Publish(IntPtr hwnd)
    {
        try
        {
            ForegroundChanged?.Invoke(hwnd);
        }
        catch
        {
            // A subscriber must not tear down the process-wide WinEvent callback.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IntPtr hook = _hook;
        _hook = IntPtr.Zero;
        if (hook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(hook);
        }

        ForegroundChanged = null;
    }

    private delegate void WinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint eventTimeMilliseconds);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
