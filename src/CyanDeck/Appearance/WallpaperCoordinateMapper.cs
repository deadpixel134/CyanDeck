namespace CyanDeck.Appearance;

/// <summary>
/// Maps a physical screen coordinate back into the original wallpaper bitmap.
/// Mapping is pure math and performs no screen capture or filesystem access.
/// </summary>
internal static class WallpaperCoordinateMapper
{
    internal static bool TryMap(
        int screenX,
        int screenY,
        PixelRect monitorBounds,
        PixelRect virtualBounds,
        PixelSize imageSize,
        WallpaperDisplayPosition position,
        out PixelPoint imagePoint,
        out bool useBackgroundColor)
    {
        imagePoint = default;
        useBackgroundColor = false;

        if (!monitorBounds.IsValid || !virtualBounds.IsValid || !imageSize.IsValid || !monitorBounds.Contains(screenX, screenY))
        {
            return false;
        }

        if (position == WallpaperDisplayPosition.Span)
        {
            return TryMapFill(screenX, screenY, virtualBounds, imageSize, out imagePoint, out useBackgroundColor);
        }

        int localX = screenX - monitorBounds.Left;
        int localY = screenY - monitorBounds.Top;
        return position switch
        {
            WallpaperDisplayPosition.Center => TryMapCenter(localX, localY, monitorBounds, imageSize, out imagePoint, out useBackgroundColor),
            WallpaperDisplayPosition.Tile => TryMapTile(screenX, screenY, virtualBounds, imageSize, out imagePoint, out useBackgroundColor),
            WallpaperDisplayPosition.Stretch => TryMapStretch(localX, localY, monitorBounds, imageSize, out imagePoint, out useBackgroundColor),
            WallpaperDisplayPosition.Fit => TryMapFit(screenX, screenY, monitorBounds, imageSize, out imagePoint, out useBackgroundColor),
            WallpaperDisplayPosition.Fill => TryMapFill(screenX, screenY, monitorBounds, imageSize, out imagePoint, out useBackgroundColor),
            _ => false,
        };
    }

    private static bool TryMapCenter(
        int localX,
        int localY,
        PixelRect monitor,
        PixelSize image,
        out PixelPoint point,
        out bool background)
    {
        point = default;
        background = false;
        double left = (monitor.Width - image.Width) / 2d;
        double top = (monitor.Height - image.Height) / 2d;
        double ix = (localX + 0.5d) - left;
        double iy = (localY + 0.5d) - top;
        if (ix < 0d || iy < 0d || ix >= image.Width || iy >= image.Height)
        {
            background = true;
            return true;
        }

        point = ClampPoint((int)Math.Floor(ix), (int)Math.Floor(iy), image);
        return true;
    }

    private static bool TryMapTile(
        int screenX,
        int screenY,
        PixelRect virtualBounds,
        PixelSize image,
        out PixelPoint point,
        out bool background)
    {
        background = false;
        int x = PositiveModulo(screenX - virtualBounds.Left, image.Width);
        int y = PositiveModulo(screenY - virtualBounds.Top, image.Height);
        point = new PixelPoint(x, y);
        return true;
    }

    private static bool TryMapStretch(
        int localX,
        int localY,
        PixelRect monitor,
        PixelSize image,
        out PixelPoint point,
        out bool background)
    {
        background = false;
        int x = (int)Math.Floor((localX + 0.5d) * image.Width / monitor.Width);
        int y = (int)Math.Floor((localY + 0.5d) * image.Height / monitor.Height);
        point = ClampPoint(x, y, image);
        return true;
    }

    private static bool TryMapFit(
        int screenX,
        int screenY,
        PixelRect destination,
        PixelSize image,
        out PixelPoint point,
        out bool background)
    {
        point = default;
        background = false;
        double scale = Math.Min(destination.Width / (double)image.Width, destination.Height / (double)image.Height);
        if (!double.IsFinite(scale) || scale <= 0d)
        {
            return false;
        }

        double drawnWidth = image.Width * scale;
        double drawnHeight = image.Height * scale;
        double left = destination.Left + ((destination.Width - drawnWidth) / 2d);
        double top = destination.Top + ((destination.Height - drawnHeight) / 2d);
        double dx = (screenX + 0.5d) - left;
        double dy = (screenY + 0.5d) - top;
        if (dx < 0d || dy < 0d || dx >= drawnWidth || dy >= drawnHeight)
        {
            background = true;
            return true;
        }

        point = ClampPoint((int)Math.Floor(dx / scale), (int)Math.Floor(dy / scale), image);
        return true;
    }

    private static bool TryMapFill(
        int screenX,
        int screenY,
        PixelRect destination,
        PixelSize image,
        out PixelPoint point,
        out bool background)
    {
        point = default;
        background = false;
        double scale = Math.Max(destination.Width / (double)image.Width, destination.Height / (double)image.Height);
        if (!double.IsFinite(scale) || scale <= 0d)
        {
            return false;
        }

        double drawnWidth = image.Width * scale;
        double drawnHeight = image.Height * scale;
        double left = destination.Left + ((destination.Width - drawnWidth) / 2d);
        double top = destination.Top + ((destination.Height - drawnHeight) / 2d);
        double ix = ((screenX + 0.5d) - left) / scale;
        double iy = ((screenY + 0.5d) - top) / scale;
        point = ClampPoint((int)Math.Floor(ix), (int)Math.Floor(iy), image);
        return true;
    }

    private static PixelPoint ClampPoint(int x, int y, PixelSize image) => new(
        Math.Clamp(x, 0, image.Width - 1),
        Math.Clamp(y, 0, image.Height - 1));

    private static int PositiveModulo(int value, int divisor)
    {
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }
}
