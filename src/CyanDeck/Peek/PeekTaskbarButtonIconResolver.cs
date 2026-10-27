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

    /// <summary>
    /// v8.0.44: user-facing check for the Settings icon picker. Returns null when the file
    /// would be used, otherwise a short Korean reason (the button then keeps the default icon).
    /// Mirrors the rules of <see cref="Resolve"/>.
    /// </summary>
    public static string? DescribeCustomIconProblem(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;
        try
        {
            var fullPath = Path.GetFullPath(configuredPath.Trim());
            if (!Path.IsPathFullyQualified(fullPath) || IsUnc(fullPath))
                return "네트워크(UNC) 경로가 아닌 이 PC의 .ico 파일만 사용할 수 있습니다.";
            if (!string.Equals(Path.GetExtension(fullPath), ".ico", StringComparison.OrdinalIgnoreCase))
                return ".ico 아이콘 파일만 사용할 수 있습니다.";
            var info = new FileInfo(fullPath);
            if (!info.Exists) return "아이콘 파일을 찾을 수 없습니다.";
            if (info.Length <= 0 || info.Length > MaximumCustomIconBytes)
                return "4MB 이하의 .ico 파일만 사용할 수 있습니다.";
            _ = LoadWpfIcon(fullPath);
            return null;
        }
        catch
        {
            return "아이콘 파일을 읽을 수 없습니다. 다른 .ico 파일을 선택하세요.";
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
