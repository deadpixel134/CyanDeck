namespace CyanDeck.Layout;

public enum ItemSurfaceLayoutKind
{
    Grid,
    FreePlacement,
    List,
    Details
}

public readonly record struct IconSpacing(double HorizontalDip, double VerticalDip)
{
    public static IconSpacing Normalize(IconSpacing value, IconSpacing fallback)
    {
        static double Safe(double v, double f)
            => double.IsFinite(v) && v >= 0 && v <= 512 ? v : f;

        return new IconSpacing(
            Safe(value.HorizontalDip, fallback.HorizontalDip),
            Safe(value.VerticalDip, fallback.VerticalDip));
    }
}

public sealed record LinkedIconSpacingSettings
{
    public bool UseSameSpacingInsideAndOutsideGroups { get; init; } = false;
}

public sealed record LinkedIconSpacingContext(
    bool MasterEnabled,
    bool IsSafeMode,
    bool CanEditLayout,
    bool UseSameSpacingInsideAndOutsideGroups,
    ItemSurfaceLayoutKind SurfaceKind,
    IconSpacing SavedGroupSpacing,
    IconSpacing DesktopLooseIconSpacing);

public readonly record struct LinkedIconSpacingDecision(
    IconSpacing EffectiveSpacing,
    bool UsesDesktopSpacing,
    bool GroupSpacingControlsEnabled,
    bool ShouldRelayoutNow);
