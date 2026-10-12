using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace DesktopDeck.Appearance;

/// <summary>
/// Owns only the wallpaper layer. Host content/title/icon visuals must remain outside this Canvas.
/// </summary>
internal sealed class WallpaperBlurBackdropPresenter
{
    private readonly Canvas _backdropCanvas;
    private readonly WallpaperBlurBitmapCache _cache;

    internal WallpaperBlurBackdropPresenter(Canvas backdropCanvas, WallpaperBlurBitmapCache cache)
    {
        _backdropCanvas = backdropCanvas ?? throw new ArgumentNullException(nameof(backdropCanvas));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _backdropCanvas.IsHitTestVisible = false;
        _backdropCanvas.ClipToBounds = true;
    }

    internal bool Apply(WallpaperBackdropPlan plan, double dpiScaleX, double dpiScaleY, double radiusDip)
    {
        if (plan is null || dpiScaleX <= 0d || dpiScaleY <= 0d || !double.IsFinite(radiusDip))
        {
            return false;
        }

        _backdropCanvas.Children.Clear();
        _backdropCanvas.Background = new SolidColorBrush(Color.FromRgb(
            plan.BackgroundColor.R,
            plan.BackgroundColor.G,
            plan.BackgroundColor.B));

        int added = 0;
        foreach (WallpaperBackdropSlice slice in plan.Slices)
        {
            if (!_cache.TryGet(slice.WallpaperPath, out BitmapSource? bitmap) || bitmap is null)
            {
                continue;
            }

            PhysicalRectD clip = slice.VisiblePhysical;
            PhysicalRectD imageRect = slice.ImageDestinationPhysical;
            if (!clip.IsValid || !imageRect.IsValid)
            {
                continue;
            }

            var clipHost = new Canvas
            {
                Width = clip.Width / dpiScaleX,
                Height = clip.Height / dpiScaleY,
                ClipToBounds = true,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(clipHost, (clip.Left - plan.GroupBoundsPhysical.Left) / dpiScaleX);
            Canvas.SetTop(clipHost, (clip.Top - plan.GroupBoundsPhysical.Top) / dpiScaleY);

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Fill,
                Width = imageRect.Width / dpiScaleX,
                Height = imageRect.Height / dpiScaleY,
                SnapsToDevicePixels = false,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(image, (imageRect.Left - clip.Left) / dpiScaleX);
            Canvas.SetTop(image, (imageRect.Top - clip.Top) / dpiScaleY);
            clipHost.Children.Add(image);
            _backdropCanvas.Children.Add(clipHost);
            added++;
        }

        if (added == 0)
        {
            _backdropCanvas.Effect = null;
            return true;
        }

        _backdropCanvas.Effect = new BlurEffect
        {
            Radius = Math.Clamp(radiusDip, 0d, 32d),
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance,
        };
        return true;
    }

    internal void Clear()
    {
        _backdropCanvas.Effect = null;
        _backdropCanvas.Children.Clear();
        _backdropCanvas.Background = Brushes.Transparent;
    }
}
