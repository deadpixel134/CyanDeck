using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopDeck.Appearance;

internal sealed class WallpaperBlurBitmapCache
{
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;

    internal bool TryGetSize(string path, out PixelSize size)
    {
        size = default;
        if (!TryGet(path, out BitmapSource? source) || source is null)
        {
            return false;
        }

        size = new PixelSize(source.PixelWidth, source.PixelHeight);
        return size.IsValid;
    }

    internal bool TryGet(string path, out BitmapSource? source)
    {
        source = null;
        if (!TryNormalizeLocalPath(path, out string normalized))
        {
            return false;
        }

        if (_entries.TryGetValue(normalized, out CacheEntry? existing))
        {
            existing.LastUse = ++_generation;
            source = existing.Source;
            return true;
        }

        if (!TryDecode(normalized, out BitmapSource? decoded) || decoded is null)
        {
            return false;
        }

        _entries[normalized] = new CacheEntry(decoded, ++_generation);
        Trim();
        source = decoded;
        return true;
    }

    internal void InvalidateAll() => _entries.Clear();

    internal int Count => _entries.Count;

    private static bool TryNormalizeLocalPath(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var uri = new Uri(path, UriKind.Absolute);
            if (!uri.IsFile || uri.IsUnc)
            {
                return false;
            }

            normalized = uri.LocalPath;
            return !string.IsNullOrWhiteSpace(normalized);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool TryDecode(string path, out BitmapSource? source)
    {
        source = null;
        try
        {
            var uri = new Uri(path, UriKind.Absolute);
            BitmapDecoder decoder = BitmapDecoder.Create(
                uri,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            BitmapFrame frame = decoder.Frames[0];
            long pixels = (long)frame.PixelWidth * frame.PixelHeight;
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
                pixels <= 0 || pixels > WallpaperBlurPolicy.MaxDecodedPixelsPerWallpaper)
            {
                return false;
            }

            BitmapSource usable = frame.Format == PixelFormats.Pbgra32 || frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0d);
            if (usable.CanFreeze)
            {
                usable.Freeze();
            }

            source = usable;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or UriFormatException
                                   or InvalidOperationException)
        {
            return false;
        }
    }

    private void Trim()
    {
        while (_entries.Count > WallpaperBlurPolicy.MaxCachedWallpapers)
        {
            KeyValuePair<string, CacheEntry> oldest = _entries.Aggregate((a, b) =>
                a.Value.LastUse <= b.Value.LastUse ? a : b);
            _entries.Remove(oldest.Key);
        }
    }

    private sealed class CacheEntry
    {
        internal CacheEntry(BitmapSource source, long lastUse)
        {
            Source = source;
            LastUse = lastUse;
        }

        internal BitmapSource Source { get; }
        internal long LastUse { get; set; }
    }
}
