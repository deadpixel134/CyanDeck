namespace CyanDeck.Appearance;

/// <summary>
/// Transient slider state for one selected group. ARGB remains the only durable color state.
/// A hue hint is retained only in this live editor session so grayscale colors can accept a hue
/// before saturation is raised; the hint is intentionally not serialized.
/// </summary>
internal sealed class BackgroundColorAdjustmentSession
{
    private BackgroundColorAdjustment _adjustment;
    private Argb32 _currentColor;

    internal BackgroundColorAdjustmentSession(Argb32 currentColor)
    {
        _currentColor = currentColor;
        _adjustment = BackgroundColorMath.FromArgb(currentColor);
    }

    internal Argb32 CurrentColor => _currentColor;
    internal BackgroundColorAdjustment Adjustment => _adjustment;

    internal Argb32 SetHue(double degrees) => Apply(_adjustment with { HueDegrees = degrees });
    internal Argb32 SetSaturation(double percent) => Apply(_adjustment with { SaturationPercent = percent });
    internal Argb32 SetBrightness(double percent) => Apply(_adjustment with { BrightnessPercent = percent });
    internal Argb32 SetTransparency(double percent) => Apply(_adjustment with { TransparencyPercent = percent });

    internal void SyncFromHost(Argb32 color)
    {
        double hueHint = _adjustment.HueDegrees;
        _currentColor = color;
        _adjustment = BackgroundColorMath.FromArgb(color, hueHint);
    }

    internal Argb32 ResetTo(Argb32 defaultColor)
    {
        _currentColor = defaultColor;
        _adjustment = BackgroundColorMath.FromArgb(defaultColor);
        return _currentColor;
    }

    private Argb32 Apply(BackgroundColorAdjustment value)
    {
        _adjustment = BackgroundColorMath.Normalize(value);
        _currentColor = BackgroundColorMath.ToArgb(_adjustment);
        return _currentColor;
    }
}
