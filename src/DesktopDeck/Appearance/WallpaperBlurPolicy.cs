namespace DesktopDeck.Appearance;

internal static class WallpaperBlurPolicy
{
    internal const bool DefaultEnabled = false;
    internal const double DefaultBlurRadiusDip = 16d;
    internal const int MaxCachedWallpapers = 4;
    internal const long MaxDecodedPixelsPerWallpaper = 100_000_000;

    internal static WallpaperBlurPolicyDecision Evaluate(WallpaperBlurPolicyInput input)
    {
        if (!input.Enabled)
        {
            return new(false, WallpaperBlurSuppressionReason.Disabled);
        }

        if (input.HighContrast)
        {
            return new(false, WallpaperBlurSuppressionReason.HighContrast);
        }

        if (input.HostTransientSuppression)
        {
            return new(false, WallpaperBlurSuppressionReason.HostTransientSuppression);
        }

        if (!input.GroupBoundsPhysical.IsValid ||
            !double.IsFinite(input.DpiScaleX) || !double.IsFinite(input.DpiScaleY) ||
            input.DpiScaleX <= 0d || input.DpiScaleY <= 0d)
        {
            return new(false, WallpaperBlurSuppressionReason.InvalidGeometry);
        }

        return new(true, WallpaperBlurSuppressionReason.None);
    }
}
