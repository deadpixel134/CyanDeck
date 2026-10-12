namespace DesktopDeck.Appearance;

internal static class BackgroundColorMath
{
    internal static BackgroundColorAdjustment FromArgb(Argb32 color, double hueHintDegrees = 0d)
    {
        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue;
        if (delta <= double.Epsilon)
        {
            hue = NormalizeHue(hueHintDegrees);
        }
        else if (max == r)
        {
            hue = 60d * (((g - b) / delta) % 6d);
        }
        else if (max == g)
        {
            hue = 60d * (((b - r) / delta) + 2d);
        }
        else
        {
            hue = 60d * (((r - g) / delta) + 4d);
        }

        hue = NormalizeHue(hue);
        double saturation = max <= double.Epsilon ? 0d : (delta / max) * 100d;
        double brightness = max * 100d;
        double transparency = (1d - (color.A / 255d)) * 100d;

        return new BackgroundColorAdjustment(
            HueDegrees: hue,
            SaturationPercent: ClampPercent(saturation),
            BrightnessPercent: ClampPercent(brightness),
            TransparencyPercent: ClampPercent(transparency));
    }

    internal static Argb32 ToArgb(BackgroundColorAdjustment value)
    {
        BackgroundColorAdjustment normalized = Normalize(value);
        double h = normalized.HueDegrees;
        double s = normalized.SaturationPercent / 100d;
        double v = normalized.BrightnessPercent / 100d;

        double chroma = v * s;
        double hPrime = h / 60d;
        double x = chroma * (1d - Math.Abs((hPrime % 2d) - 1d));

        (double r1, double g1, double b1) = hPrime switch
        {
            >= 0d and < 1d => (chroma, x, 0d),
            >= 1d and < 2d => (x, chroma, 0d),
            >= 2d and < 3d => (0d, chroma, x),
            >= 3d and < 4d => (0d, x, chroma),
            >= 4d and < 5d => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };

        double m = v - chroma;
        byte a = ToByte((1d - (normalized.TransparencyPercent / 100d)) * 255d);
        byte r = ToByte((r1 + m) * 255d);
        byte g = ToByte((g1 + m) * 255d);
        byte b = ToByte((b1 + m) * 255d);
        return new Argb32(a, r, g, b);
    }

    internal static BackgroundColorAdjustment Normalize(BackgroundColorAdjustment value)
    {
        return new BackgroundColorAdjustment(
            HueDegrees: NormalizeHue(value.HueDegrees),
            SaturationPercent: ClampPercent(value.SaturationPercent),
            BrightnessPercent: ClampPercent(value.BrightnessPercent),
            TransparencyPercent: ClampPercent(value.TransparencyPercent));
    }

    internal static double NormalizeHue(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        double normalized = value % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    internal static double ClampPercent(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 100d);
    }

    private static byte ToByte(double value)
    {
        double bounded = Math.Clamp(value, 0d, 255d);
        return (byte)Math.Round(bounded, MidpointRounding.AwayFromZero);
    }
}
