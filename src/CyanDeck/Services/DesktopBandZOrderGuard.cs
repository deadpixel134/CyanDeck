using System.Runtime.InteropServices;
using System.Windows.Interop;
using CyanDeck.Interop;

namespace CyanDeck.Services;

/// <summary>
/// A desktop-band WPF top-level window is allowed to move in Z-order only when
/// DesktopHost explicitly positions it relative to Explorer. WPF/Windows may
/// otherwise promote it when clicked, shown, or activated. Suppress that
/// unsolicited WINDOWPOS change *before* it can cover an ordinary app window;
/// do not react afterwards with a periodic SetWindowPos/focus-stealing loop.
/// This hook is installed only on CyanDeck's registered desktop surfaces.
/// </summary>
internal sealed class DesktopBandZOrderGuard : IDisposable
{
    private const int WM_WINDOWPOSCHANGING = 0x0046;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private int _hostPlacementDepth;
    private bool _disposed;

    internal DesktopBandZOrderGuard(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd)
            ?? throw new InvalidOperationException("Desktop surface HWND has no WPF message source.");
        _hook = OnWindowMessage;
        _source.AddHook(_hook);
    }

    internal IDisposable AllowHostPlacement()
    {
        _hostPlacementDepth++;
        return new Scope(this);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || _hostPlacementDepth != 0 || message != WM_WINDOWPOSCHANGING || lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((pos.Flags & NativeMethods.SWP_NOZORDER) != 0)
            return IntPtr.Zero;

        // Keep location, size, hide/show, and frame flags unmodified. Only
        // prevent an unsolicited promotion above a regular application.
        pos.Flags |= NativeMethods.SWP_NOZORDER;
        Marshal.StructureToPtr(pos, lParam, false);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_source.IsDisposed) _source.RemoveHook(_hook);
    }

    private sealed class Scope : IDisposable
    {
        private DesktopBandZOrderGuard? _owner;
        internal Scope(DesktopBandZOrderGuard owner) => _owner = owner;
        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            if (owner is not null) owner._hostPlacementDepth--;
        }
    }
}
