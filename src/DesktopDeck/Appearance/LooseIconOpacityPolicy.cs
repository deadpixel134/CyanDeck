namespace DesktopDeck.Appearance;

// Controls only WPF icon content opacity. Never touches HWND, Z-order or activation.
// The fallback preserves the pre-existing standalone Recycle Bin behavior for
// upgraded users who have not enabled the independent icon Chameleon.
internal readonly record struct LooseIconOpacityOptions(
    bool Enabled,
    double IdleOpacity,
    double HoverOpacity,
    bool ApplyToRecycleBin,
    double LegacyRecycleIdleOpacity)
{
    internal double Resolve(bool isRecycleBin, bool interacting)
    {
        if (Enabled && (!isRecycleBin || ApplyToRecycleBin))
            return Normalize(interacting ? HoverOpacity : IdleOpacity);
        return isRecycleBin && !interacting ? Normalize(LegacyRecycleIdleOpacity) : 1d;
    }

    private static double Normalize(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0.15, 1d) : 1d;
}
