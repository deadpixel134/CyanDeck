namespace DesktopDeck.Appearance;

internal sealed class WallpaperColorSampleService
{
    private readonly DesktopWallpaperSnapshotReader _snapshotReader;
    private readonly WallpaperBitmapSampler _bitmapSampler;

    internal WallpaperColorSampleService(
        DesktopWallpaperSnapshotReader snapshotReader,
        WallpaperBitmapSampler bitmapSampler)
    {
        _snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
        _bitmapSampler = bitmapSampler ?? throw new ArgumentNullException(nameof(bitmapSampler));
    }

    internal bool TrySampleAt(int screenX, int screenY, out WallpaperSample sample)
    {
        sample = default;
        if (!_snapshotReader.TryRead(out WallpaperDesktopSnapshot? snapshot) || snapshot is null)
        {
            return false;
        }

        WallpaperMonitorSnapshot monitor = default;
        bool foundMonitor = false;
        foreach (WallpaperMonitorSnapshot candidate in snapshot.Monitors)
        {
            if (!candidate.Bounds.Contains(screenX, screenY))
            {
                continue;
            }

            monitor = candidate;
            foundMonitor = true;
            break;
        }

        if (!foundMonitor)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(monitor.WallpaperPath))
        {
            sample = new WallpaperSample(snapshot.BackgroundColor, WallpaperSampleSource.DesktopBackgroundColor);
            return true;
        }

        bool ok = _bitmapSampler.TrySample(
            monitor.WallpaperPath,
            size =>
            {
                bool mapped = WallpaperCoordinateMapper.TryMap(
                    screenX,
                    screenY,
                    monitor.Bounds,
                    snapshot.VirtualBounds,
                    size,
                    snapshot.Position,
                    out PixelPoint point,
                    out bool useBackground);
                return (mapped, point, useBackground);
            },
            out Rgb24 sampledColor,
            out bool useBackgroundColor);

        if (!ok)
        {
            return false;
        }

        sample = useBackgroundColor
            ? new WallpaperSample(snapshot.BackgroundColor, WallpaperSampleSource.DesktopBackgroundColor)
            : new WallpaperSample(sampledColor, WallpaperSampleSource.WallpaperImage);
        return true;
    }
}
