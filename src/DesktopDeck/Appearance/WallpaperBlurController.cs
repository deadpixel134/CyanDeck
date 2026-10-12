namespace DesktopDeck.Appearance;

internal sealed class WallpaperBlurController
{
    private readonly DesktopWallpaperSnapshotReader _snapshotReader;
    private readonly WallpaperBackdropSlicePlanner _planner;
    private readonly WallpaperBlurBitmapCache _cache;
    private readonly WallpaperBlurBackdropPresenter _presenter;

    internal WallpaperBlurController(
        DesktopWallpaperSnapshotReader snapshotReader,
        WallpaperBackdropSlicePlanner planner,
        WallpaperBlurBitmapCache cache,
        WallpaperBlurBackdropPresenter presenter)
    {
        _snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    internal WallpaperBlurPolicyDecision Refresh(WallpaperBlurPolicyInput input)
    {
        WallpaperBlurPolicyDecision decision = WallpaperBlurPolicy.Evaluate(input);
        if (!decision.RenderBlur)
        {
            _presenter.Clear();
            return decision;
        }

        if (!_snapshotReader.TryRead(out WallpaperDesktopSnapshot? snapshot) || snapshot is null)
        {
            _presenter.Clear();
            return new(false, WallpaperBlurSuppressionReason.WallpaperUnavailable);
        }

        PixelSize? ResolveSize(string path) => _cache.TryGetSize(path, out PixelSize size) ? size : null;
        if (!_planner.TryPlan(snapshot, input.GroupBoundsPhysical, ResolveSize, out WallpaperBackdropPlan? plan) || plan is null)
        {
            _presenter.Clear();
            return new(false, WallpaperBlurSuppressionReason.WallpaperUnavailable);
        }

        if (!_presenter.Apply(plan, input.DpiScaleX, input.DpiScaleY, WallpaperBlurPolicy.DefaultBlurRadiusDip))
        {
            _presenter.Clear();
            return new(false, WallpaperBlurSuppressionReason.WallpaperUnavailable);
        }

        return decision;
    }

    internal void InvalidateWallpaper()
    {
        _cache.InvalidateAll();
        _presenter.Clear();
    }
}
