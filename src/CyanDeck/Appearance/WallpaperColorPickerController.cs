namespace CyanDeck.Appearance;

/// <summary>
/// One-shot command boundary used after the host's transparent eyedropper surface supplies a
/// physical screen coordinate. RGB is applied through the existing appearance command; the host
/// must preserve the group's current alpha/transparency component.
/// </summary>
internal sealed class WallpaperColorPickerController
{
    private readonly WallpaperColorSampleService _sampleService;
    private readonly Func<bool> _canEditAppearance;
    private readonly Action<Rgb24> _applyExistingBackgroundRgbPreservingAlpha;

    internal WallpaperColorPickerController(
        WallpaperColorSampleService sampleService,
        Func<bool> canEditAppearance,
        Action<Rgb24> applyExistingBackgroundRgbPreservingAlpha)
    {
        _sampleService = sampleService ?? throw new ArgumentNullException(nameof(sampleService));
        _canEditAppearance = canEditAppearance ?? throw new ArgumentNullException(nameof(canEditAppearance));
        _applyExistingBackgroundRgbPreservingAlpha = applyExistingBackgroundRgbPreservingAlpha
            ?? throw new ArgumentNullException(nameof(applyExistingBackgroundRgbPreservingAlpha));
    }

    internal WallpaperPickStatus TryApplyAt(int physicalScreenX, int physicalScreenY)
    {
        bool canEdit;
        try { canEdit = _canEditAppearance(); }
        catch { canEdit = false; }

        if (!canEdit)
        {
            return WallpaperPickStatus.Blocked;
        }

        if (!_sampleService.TrySampleAt(physicalScreenX, physicalScreenY, out WallpaperSample sample))
        {
            return WallpaperPickStatus.UnsupportedWallpaper;
        }

        try
        {
            _applyExistingBackgroundRgbPreservingAlpha(sample.Color);
            return WallpaperPickStatus.Applied;
        }
        catch
        {
            return WallpaperPickStatus.ApplyFailed;
        }
    }
}
