namespace CyanDeck.Appearance;

/// <summary>
/// Compact sRGB color used at the v7.26 integration boundary. The full host may convert
/// to/from System.Windows.Media.Color without changing CyanDeck's persisted appearance model.
/// </summary>
internal readonly record struct Argb32(byte A, byte R, byte G, byte B);

/// <summary>
/// Transient editor representation. These values are not new persisted settings.
/// Brightness is the HSV value component. Transparency is the inverse of alpha.
/// </summary>
internal readonly record struct BackgroundColorAdjustment(
    double HueDegrees,
    double SaturationPercent,
    double BrightnessPercent,
    double TransparencyPercent);
