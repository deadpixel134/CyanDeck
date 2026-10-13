namespace CyanDeck.Appearance;

internal sealed class WallpaperBackdropSlicePlanner
{
    internal bool TryPlan(
        WallpaperDesktopSnapshot snapshot,
        PixelRect groupBounds,
        Func<string, PixelSize?> imageSizeResolver,
        out WallpaperBackdropPlan? plan)
    {
        plan = null;
        if (snapshot is null || !groupBounds.IsValid || imageSizeResolver is null)
        {
            return false;
        }

        var slices = new List<WallpaperBackdropSlice>();
        PhysicalRectD group = ToDouble(groupBounds);

        foreach (WallpaperMonitorSnapshot monitor in snapshot.Monitors)
        {
            PhysicalRectD visibleMonitor = group.Intersect(monitor.Bounds);
            if (!visibleMonitor.IsValid || string.IsNullOrWhiteSpace(monitor.WallpaperPath))
            {
                continue;
            }

            PixelSize? maybeSize = imageSizeResolver(monitor.WallpaperPath);
            if (maybeSize is not PixelSize size || !size.IsValid)
            {
                continue;
            }

            if (snapshot.Position == WallpaperDisplayPosition.Tile)
            {
                AddTileSlices(slices, monitor.WallpaperPath, visibleMonitor, snapshot.VirtualBounds, size);
                continue;
            }

            PhysicalRectD destination = snapshot.Position switch
            {
                WallpaperDisplayPosition.Center => CenterDestination(monitor.Bounds, size),
                WallpaperDisplayPosition.Stretch => ToDouble(monitor.Bounds),
                WallpaperDisplayPosition.Fit => FitDestination(monitor.Bounds, size),
                WallpaperDisplayPosition.Fill => FillDestination(monitor.Bounds, size),
                WallpaperDisplayPosition.Span => FillDestination(snapshot.VirtualBounds, size),
                _ => default,
            };

            if (!destination.IsValid)
            {
                continue;
            }

            PhysicalRectD visible = visibleMonitor.Intersect(destination);
            if (visible.IsValid)
            {
                slices.Add(new WallpaperBackdropSlice(monitor.WallpaperPath, destination, visible));
            }
        }

        plan = new WallpaperBackdropPlan(groupBounds, snapshot.BackgroundColor, slices.AsReadOnly());
        return true;
    }

    private static void AddTileSlices(
        List<WallpaperBackdropSlice> slices,
        string path,
        PhysicalRectD visibleMonitor,
        PixelRect virtualBounds,
        PixelSize image)
    {
        double originX = virtualBounds.Left;
        double originY = virtualBounds.Top;
        int firstX = (int)Math.Floor((visibleMonitor.Left - originX) / image.Width);
        int lastX = (int)Math.Floor((Math.BitDecrement(visibleMonitor.Right) - originX) / image.Width);
        int firstY = (int)Math.Floor((visibleMonitor.Top - originY) / image.Height);
        int lastY = (int)Math.Floor((Math.BitDecrement(visibleMonitor.Bottom) - originY) / image.Height);

        for (int y = firstY; y <= lastY; y++)
        {
            for (int x = firstX; x <= lastX; x++)
            {
                var destination = new PhysicalRectD(
                    originX + (x * image.Width),
                    originY + (y * image.Height),
                    originX + ((x + 1) * image.Width),
                    originY + ((y + 1) * image.Height));
                PhysicalRectD visible = visibleMonitor.Intersect(destination);
                if (visible.IsValid)
                {
                    slices.Add(new WallpaperBackdropSlice(path, destination, visible));
                }
            }
        }
    }

    private static PhysicalRectD CenterDestination(PixelRect target, PixelSize image)
    {
        double left = target.Left + ((target.Width - image.Width) / 2d);
        double top = target.Top + ((target.Height - image.Height) / 2d);
        return new PhysicalRectD(left, top, left + image.Width, top + image.Height);
    }

    private static PhysicalRectD FitDestination(PixelRect target, PixelSize image)
    {
        double scale = Math.Min(target.Width / (double)image.Width, target.Height / (double)image.Height);
        return ScaledCenteredDestination(target, image, scale);
    }

    private static PhysicalRectD FillDestination(PixelRect target, PixelSize image)
    {
        double scale = Math.Max(target.Width / (double)image.Width, target.Height / (double)image.Height);
        return ScaledCenteredDestination(target, image, scale);
    }

    private static PhysicalRectD ScaledCenteredDestination(PixelRect target, PixelSize image, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0d)
        {
            return default;
        }

        double width = image.Width * scale;
        double height = image.Height * scale;
        double left = target.Left + ((target.Width - width) / 2d);
        double top = target.Top + ((target.Height - height) / 2d);
        return new PhysicalRectD(left, top, left + width, top + height);
    }

    private static PhysicalRectD ToDouble(PixelRect rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
