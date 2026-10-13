using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CyanDeck.Snapshots;

/// <summary>
/// Captures the current virtual screen exactly as Windows is presenting it and downscales
/// directly in GDI. This intentionally does not manipulate CyanDeck visibility, focus,
/// Explorer, or other applications in order to obtain a prettier thumbnail.
/// </summary>
internal static class GdiVirtualDesktopThumbnailCapture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int Halftone = 4;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    internal static bool TryCapture(
        SnapshotScreenPreviewOptions options,
        out SnapshotScreenPreviewCapture capture,
        out string? errorKind)
    {
        capture = default;
        errorKind = null;
        options = (options ?? new SnapshotScreenPreviewOptions()).Normalize();

        if (!OperatingSystem.IsWindows())
        {
            errorKind = "unsupported-platform";
            return false;
        }

        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);
        var sourceBounds = new VirtualDesktopPixelRect(left, top, width, height);
        if (!sourceBounds.IsValid)
        {
            errorKind = "invalid-virtual-screen";
            return false;
        }

        ComputeThumbnailSize(
            width,
            height,
            options.MaxThumbnailWidthPx,
            options.MaxThumbnailHeightPx,
            out int targetWidth,
            out int targetHeight);

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousObject = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                errorKind = "get-screen-dc";
                return false;
            }

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                errorKind = "create-compatible-dc";
                return false;
            }

            bitmap = CreateCompatibleBitmap(screenDc, targetWidth, targetHeight);
            if (bitmap == IntPtr.Zero)
            {
                errorKind = "create-compatible-bitmap";
                return false;
            }

            previousObject = SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero || previousObject == new IntPtr(-1))
            {
                errorKind = "select-bitmap";
                return false;
            }

            SetStretchBltMode(memoryDc, Halftone);
            _ = SetBrushOrgEx(memoryDc, 0, 0, IntPtr.Zero);

            if (!StretchBlt(
                    memoryDc,
                    0,
                    0,
                    targetWidth,
                    targetHeight,
                    screenDc,
                    left,
                    top,
                    width,
                    height,
                    SrcCopy | CaptureBlt))
            {
                errorKind = "stretch-blt";
                return false;
            }

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length <= 0 || stream.Length > options.MaxEncodedBytes)
            {
                errorKind = "encoded-size-limit";
                return false;
            }

            byte[] pngBytes = stream.ToArray();
            capture = new SnapshotScreenPreviewCapture(
                pngBytes,
                sourceBounds,
                targetWidth,
                targetHeight);
            return capture.IsValid;
        }
        catch (ExternalException)
        {
            errorKind = "gdi-external-exception";
            return false;
        }
        catch (IOException)
        {
            errorKind = "encode-io";
            return false;
        }
        catch (OutOfMemoryException)
        {
            errorKind = "out-of-memory";
            return false;
        }
        finally
        {
            if (previousObject != IntPtr.Zero && previousObject != new IntPtr(-1) && memoryDc != IntPtr.Zero)
            {
                _ = SelectObject(memoryDc, previousObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                _ = ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    internal static void ComputeThumbnailSize(
        int sourceWidth,
        int sourceHeight,
        int maxWidth,
        int maxHeight,
        out int targetWidth,
        out int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            targetWidth = 1;
            targetHeight = 1;
            return;
        }

        double scale = Math.Min(
            1.0,
            Math.Min((double)Math.Max(1, maxWidth) / sourceWidth, (double)Math.Max(1, maxHeight) / sourceHeight));

        targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale, MidpointRounding.AwayFromZero));
        targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale, MidpointRounding.AwayFromZero));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr oldPoint);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int widthDest,
        int heightDest,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int widthSrc,
        int heightSrc,
        uint rop);
}
