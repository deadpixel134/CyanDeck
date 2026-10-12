using System.Runtime.InteropServices;

namespace DesktopDeck.Appearance;

internal static class DesktopWallpaperInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    internal enum NativeWallpaperPosition
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5,
    }

    internal enum DesktopSlideshowDirection
    {
        Forward = 0,
        Backward = 1,
    }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDesktopWallpaper
    {
        [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
        [PreserveSig] int GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out NativeRect displayRect);
        [PreserveSig] int SetBackgroundColor(uint color);
        [PreserveSig] int GetBackgroundColor(out uint color);
        [PreserveSig] int SetPosition(NativeWallpaperPosition position);
        [PreserveSig] int GetPosition(out NativeWallpaperPosition position);
        [PreserveSig] int SetSlideshow(IntPtr items);
        [PreserveSig] int GetSlideshow(out IntPtr items);
        [PreserveSig] int SetSlideshowOptions(uint options, uint slideshowTick);
        [PreserveSig] int GetSlideshowOptions(out uint options, out uint slideshowTick);
        [PreserveSig] int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, DesktopSlideshowDirection direction);
        [PreserveSig] int GetStatus(out uint state);
        [PreserveSig] int Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [ComImport]
    [Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
    internal class DesktopWallpaperComObject
    {
    }
}
