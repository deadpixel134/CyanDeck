namespace CyanDeck.Appearance;

internal enum WallpaperDisplayPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

internal enum WallpaperSampleSource
{
    WallpaperImage = 0,
    DesktopBackgroundColor = 1,
}

internal readonly record struct Rgb24(byte R, byte G, byte B);
internal readonly record struct PixelPoint(int X, int Y);
internal readonly record struct PixelSize(int Width, int Height)
{
    internal bool IsValid => Width > 0 && Height > 0;
}

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
    internal bool IsValid => Width > 0 && Height > 0;
    internal bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    internal static PixelRect Union(PixelRect a, PixelRect b) => new(
        Math.Min(a.Left, b.Left),
        Math.Min(a.Top, b.Top),
        Math.Max(a.Right, b.Right),
        Math.Max(a.Bottom, b.Bottom));
}

internal readonly record struct WallpaperMonitorSnapshot(string MonitorId, PixelRect Bounds, string WallpaperPath);

internal sealed record WallpaperDesktopSnapshot(
    IReadOnlyList<WallpaperMonitorSnapshot> Monitors,
    PixelRect VirtualBounds,
    WallpaperDisplayPosition Position,
    Rgb24 BackgroundColor);

internal readonly record struct WallpaperSample(Rgb24 Color, WallpaperSampleSource Source);

internal enum WallpaperPickStatus
{
    Applied = 0,
    Blocked = 1,
    NoWallpaperAtPoint = 2,
    UnsupportedWallpaper = 3,
    ApplyFailed = 4,
}
