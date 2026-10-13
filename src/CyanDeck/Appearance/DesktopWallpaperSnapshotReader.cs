using System.Runtime.InteropServices;

namespace CyanDeck.Appearance;

internal sealed class DesktopWallpaperSnapshotReader
{
    internal bool TryRead(out WallpaperDesktopSnapshot? snapshot)
    {
        snapshot = null;
        DesktopWallpaperInterop.IDesktopWallpaper? wallpaper = null;
        object? comObject = null;
        try
        {
            comObject = new DesktopWallpaperInterop.DesktopWallpaperComObject();
            wallpaper = (DesktopWallpaperInterop.IDesktopWallpaper)comObject;

            if (wallpaper.GetMonitorDevicePathCount(out uint count) < 0 || count == 0)
            {
                return false;
            }

            var monitors = new List<WallpaperMonitorSnapshot>();
            PixelRect virtualBounds = default;
            bool haveVirtualBounds = false;

            for (uint i = 0; i < count; i++)
            {
                if (wallpaper.GetMonitorDevicePathAt(i, out string monitorId) < 0 || string.IsNullOrWhiteSpace(monitorId))
                {
                    continue;
                }

                int rectHr = wallpaper.GetMonitorRECT(monitorId, out DesktopWallpaperInterop.NativeRect nativeRect);
                PixelRect bounds = nativeRect.ToPixelRect();
                if (rectHr != 0 || !bounds.IsValid)
                {
                    // S_FALSE is documented for detached monitors. Treat any non-S_OK rectangle as unavailable.
                    continue;
                }

                string path = string.Empty;
                if (wallpaper.GetWallpaper(monitorId, out string resolvedPath) >= 0)
                {
                    path = resolvedPath ?? string.Empty;
                }

                monitors.Add(new WallpaperMonitorSnapshot(monitorId, bounds, path));
                virtualBounds = haveVirtualBounds ? PixelRect.Union(virtualBounds, bounds) : bounds;
                haveVirtualBounds = true;
            }

            if (monitors.Count == 0 || !haveVirtualBounds)
            {
                return false;
            }

            if (wallpaper.GetPosition(out DesktopWallpaperInterop.NativeWallpaperPosition nativePosition) < 0)
            {
                return false;
            }

            if (wallpaper.GetBackgroundColor(out uint colorRef) < 0)
            {
                colorRef = 0;
            }

            snapshot = new WallpaperDesktopSnapshot(
                monitors.AsReadOnly(),
                virtualBounds,
                ToPosition(nativePosition),
                FromColorRef(colorRef));
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                try { Marshal.FinalReleaseComObject(comObject); } catch { }
            }
        }
    }

    private static WallpaperDisplayPosition ToPosition(DesktopWallpaperInterop.NativeWallpaperPosition value) => value switch
    {
        DesktopWallpaperInterop.NativeWallpaperPosition.Center => WallpaperDisplayPosition.Center,
        DesktopWallpaperInterop.NativeWallpaperPosition.Tile => WallpaperDisplayPosition.Tile,
        DesktopWallpaperInterop.NativeWallpaperPosition.Stretch => WallpaperDisplayPosition.Stretch,
        DesktopWallpaperInterop.NativeWallpaperPosition.Fit => WallpaperDisplayPosition.Fit,
        DesktopWallpaperInterop.NativeWallpaperPosition.Fill => WallpaperDisplayPosition.Fill,
        DesktopWallpaperInterop.NativeWallpaperPosition.Span => WallpaperDisplayPosition.Span,
        _ => WallpaperDisplayPosition.Fill,
    };

    private static Rgb24 FromColorRef(uint value) => new(
        R: (byte)(value & 0xFFu),
        G: (byte)((value >> 8) & 0xFFu),
        B: (byte)((value >> 16) & 0xFFu));
}
