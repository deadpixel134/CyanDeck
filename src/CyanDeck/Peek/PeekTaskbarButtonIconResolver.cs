using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CyanDeck.Peek;

public static class PeekTaskbarButtonIconResolver
{
    public const long MaximumCustomIconBytes = 4L * 1024L * 1024L;

    public static PeekTaskbarButtonIcon Resolve(string executablePath, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executablePath));
        }

        var fallback = new PeekTaskbarButtonIcon(
            RelaunchIconResource: $"{Path.GetFullPath(executablePath)},0",
            CustomIconPath: null,
            UsesCustomIcon: false);

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return fallback;
        }

        try
        {
            var fullPath = Path.GetFullPath(configuredPath.Trim());
            if (!Path.IsPathFullyQualified(fullPath) || IsUnc(fullPath))
            {
                return fallback;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".ico", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumCustomIconBytes)
            {
                return fallback;
            }

            // Force decode now so a malformed file falls back before the taskbar metadata is changed.
            _ = LoadWpfIcon(fullPath);
            return new PeekTaskbarButtonIcon($"{fullPath},0", fullPath, true);
        }
        catch
        {
            return fallback;
        }
    }

    public static ImageSource? TryLoadImageSource(PeekTaskbarButtonIcon icon)
    {
        if (!icon.UsesCustomIcon || string.IsNullOrWhiteSpace(icon.CustomIconPath))
        {
            return null;
        }

        try
        {
            return LoadWpfIcon(icon.CustomIconPath);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource LoadWpfIcon(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            options: FileOptions.SequentialScan);

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The icon contains no image frames.");
        }

        BitmapFrame best = decoder.Frames[0];
        foreach (var frame in decoder.Frames)
        {
            if ((long)frame.PixelWidth * frame.PixelHeight > (long)best.PixelWidth * best.PixelHeight)
            {
                best = frame;
            }
        }

        if (best.CanFreeze)
        {
            best.Freeze();
        }

        return best;
    }

    private static bool IsUnc(string path) => path.StartsWith("\\\\", StringComparison.Ordinal);
}
