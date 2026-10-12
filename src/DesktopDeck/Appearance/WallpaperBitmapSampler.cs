using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDeck.Appearance;

internal sealed class WallpaperBitmapSampler
{
    private const long MaxDecodedPixels = 100_000_000;

    internal bool TrySample(
        string wallpaperPath,
        Func<PixelSize, (bool Success, PixelPoint Point, bool UseBackground)> map,
        out Rgb24 color,
        out bool useBackground)
    {
        color = default;
        useBackground = false;

        if (string.IsNullOrWhiteSpace(wallpaperPath))
        {
            useBackground = true;
            return true;
        }

        try
        {
            var uri = new Uri(wallpaperPath, UriKind.Absolute);
            if (!uri.IsFile || uri.IsUnc)
            {
                return false;
            }

            BitmapDecoder decoder = BitmapDecoder.Create(
                uri,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            var size = new PixelSize(frame.PixelWidth, frame.PixelHeight);
            long pixels = (long)size.Width * size.Height;
            if (!size.IsValid || pixels <= 0 || pixels > MaxDecodedPixels)
            {
                return false;
            }

            (bool mapped, PixelPoint point, bool background) = map(size);
            if (!mapped)
            {
                return false;
            }

            if (background)
            {
                useBackground = true;
                return true;
            }

            if (point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height)
            {
                return false;
            }

            BitmapSource source = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0d);

            byte[] pixel = new byte[4];
            source.CopyPixels(new Int32Rect(point.X, point.Y, 1, 1), pixel, 4, 0);
            color = new Rgb24(pixel[2], pixel[1], pixel[0]);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or UriFormatException
                                   or InvalidOperationException
                                   or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
