using System;
using System.Runtime.InteropServices;

namespace CyanDeck.Peek;

public static class WindowsPrimaryTaskbarReader
{
    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABS_AUTOHIDE = 0x0000001;

    public static bool TryRead(out PrimaryTaskbarMetric metric)
    {
        metric = default;

        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        };

        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == UIntPtr.Zero)
        {
            return false;
        }

        if (data.uEdge > (uint)TaskbarEdge.Bottom)
        {
            return false;
        }

        var bounds = new PixelRect(data.rc.left, data.rc.top, data.rc.right, data.rc.bottom);
        if (!bounds.IsValid)
        {
            return false;
        }

        var stateData = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        };
        var state = unchecked((uint)SHAppBarMessage(ABM_GETSTATE, ref stateData).ToUInt64());
        var autoHide = (state & ABS_AUTOHIDE) != 0;

        metric = new PrimaryTaskbarMetric((TaskbarEdge)data.uEdge, bounds, autoHide);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
