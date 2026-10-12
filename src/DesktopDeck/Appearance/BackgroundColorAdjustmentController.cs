namespace DesktopDeck.Appearance;

/// <summary>
/// Settings/UI integration adapter. The host owns the actual sliders and existing appearance
/// command/state writer. This controller performs no persistence and no filesystem work.
/// </summary>
internal sealed class BackgroundColorAdjustmentController
{
    private readonly Func<Argb32> _readCurrentColor;
    private readonly Func<Argb32> _readDefaultColor;
    private readonly Action<Argb32> _applyExistingAppearanceColor;
    private BackgroundColorAdjustmentSession _session;

    internal BackgroundColorAdjustmentController(
        Func<Argb32> readCurrentColor,
        Func<Argb32> readDefaultColor,
        Action<Argb32> applyExistingAppearanceColor)
    {
        _readCurrentColor = readCurrentColor ?? throw new ArgumentNullException(nameof(readCurrentColor));
        _readDefaultColor = readDefaultColor ?? throw new ArgumentNullException(nameof(readDefaultColor));
        _applyExistingAppearanceColor = applyExistingAppearanceColor
            ?? throw new ArgumentNullException(nameof(applyExistingAppearanceColor));

        _session = new BackgroundColorAdjustmentSession(SafeReadCurrentColor());
    }

    internal BackgroundColorAdjustment Current => _session.Adjustment;

    internal bool CanReset
    {
        get
        {
            Argb32 current = _session.CurrentColor;
            Argb32 defaults = SafeReadDefaultColor(current);
            return current != defaults;
        }
    }

    internal void RefreshFromHost()
    {
        _session.SyncFromHost(SafeReadCurrentColor());
    }

    internal void SetHue(double degrees) => Apply(_session.SetHue(degrees));
    internal void SetSaturation(double percent) => Apply(_session.SetSaturation(percent));
    internal void SetBrightness(double percent) => Apply(_session.SetBrightness(percent));
    internal void SetTransparency(double percent) => Apply(_session.SetTransparency(percent));

    internal void Reset()
    {
        Argb32 defaults = SafeReadDefaultColor(_session.CurrentColor);
        Apply(_session.ResetTo(defaults));
    }

    private void Apply(Argb32 color)
    {
        _applyExistingAppearanceColor(color);
    }

    private Argb32 SafeReadCurrentColor()
    {
        try
        {
            return _readCurrentColor();
        }
        catch
        {
            // Fail closed to an opaque neutral color only for the editor surface. Do not persist
            // this fallback unless the user subsequently changes a slider.
            return new Argb32(255, 64, 64, 64);
        }
    }

    private Argb32 SafeReadDefaultColor(Argb32 fallback)
    {
        try
        {
            return _readDefaultColor();
        }
        catch
        {
            // If the host cannot resolve defaults, Reset becomes a no-op instead of inventing
            // a product default and overwriting the user's appearance.
            return fallback;
        }
    }
}
