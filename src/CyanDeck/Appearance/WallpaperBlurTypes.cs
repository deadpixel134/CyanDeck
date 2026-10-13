namespace CyanDeck.Appearance;

internal enum WallpaperBlurSuppressionReason
{
    None = 0,
    Disabled = 1,
    HighContrast = 2,
    HostTransientSuppression = 3,
    InvalidGeometry = 4,
    WallpaperUnavailable = 5,
}

internal readonly record struct PhysicalRectD(double Left, double Top, double Right, double Bottom)
{
    internal double Width => Right - Left;
    internal double Height => Bottom - Top;
    internal bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) &&
                             double.IsFinite(Right) && double.IsFinite(Bottom) &&
                             Width > 0d && Height > 0d;

    internal PhysicalRectD Intersect(PixelRect other)
    {
        double left = Math.Max(Left, other.Left);
        double top = Math.Max(Top, other.Top);
        double right = Math.Min(Right, other.Right);
        double bottom = Math.Min(Bottom, other.Bottom);
        return new PhysicalRectD(left, top, right, bottom);
    }

    internal PhysicalRectD Intersect(PhysicalRectD other)
    {
        double left = Math.Max(Left, other.Left);
        double top = Math.Max(Top, other.Top);
        double right = Math.Min(Right, other.Right);
        double bottom = Math.Min(Bottom, other.Bottom);
        return new PhysicalRectD(left, top, right, bottom);
    }
}

internal sealed record WallpaperBackdropSlice(
    string WallpaperPath,
    PhysicalRectD ImageDestinationPhysical,
    PhysicalRectD VisiblePhysical);

internal sealed record WallpaperBackdropPlan(
    PixelRect GroupBoundsPhysical,
    Rgb24 BackgroundColor,
    IReadOnlyList<WallpaperBackdropSlice> Slices);

internal readonly record struct WallpaperBlurPolicyInput(
    bool Enabled,
    bool HighContrast,
    bool HostTransientSuppression,
    PixelRect GroupBoundsPhysical,
    double DpiScaleX,
    double DpiScaleY);

internal readonly record struct WallpaperBlurPolicyDecision(
    bool RenderBlur,
    WallpaperBlurSuppressionReason SuppressionReason);
