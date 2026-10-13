namespace CyanDeck.Layout;

public interface ILinkedIconSpacingHost
{
    bool MasterEnabled { get; }
    bool IsSafeMode { get; }
    bool CanEditLayout { get; }
    bool UseSameSpacingInsideAndOutsideGroups { get; }
    IconSpacing GetLooseDesktopIconSpacing();
    IEnumerable<LinkedSpacingSurfaceSnapshot> EnumerateGroupSurfaces();
    void SetUseSameSpacingInsideAndOutsideGroups(bool value);
    void RefreshGridSurface(string surfaceId);
    void RefreshSpacingEditorState();
    void SaveState();
}

public readonly record struct LinkedSpacingSurfaceSnapshot(
    string SurfaceId,
    ItemSurfaceLayoutKind SurfaceKind,
    IconSpacing SavedGroupSpacing);

public sealed class LinkedIconSpacingCoordinator
{
    private readonly ILinkedIconSpacingHost _host;

    public LinkedIconSpacingCoordinator(ILinkedIconSpacingHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    public bool TrySetLinked(bool enabled)
    {
        if (!_host.MasterEnabled || _host.IsSafeMode || !_host.CanEditLayout)
            return false;
        if (_host.UseSameSpacingInsideAndOutsideGroups == enabled)
            return true;

        var oldValue = _host.UseSameSpacingInsideAndOutsideGroups;
        try
        {
            _host.SetUseSameSpacingInsideAndOutsideGroups(enabled);
            RefreshAffectedGridSurfaces();
            _host.RefreshSpacingEditorState();
            _host.SaveState();
            return true;
        }
        catch
        {
            try
            {
                _host.SetUseSameSpacingInsideAndOutsideGroups(oldValue);
                RefreshAffectedGridSurfaces();
                _host.RefreshSpacingEditorState();
            }
            catch
            {
                // The host owns recovery/logging. Do not overwrite saved per-group spacing here.
            }
            return false;
        }
    }

    public void OnLooseDesktopSpacingChanged()
    {
        if (!_host.MasterEnabled || _host.IsSafeMode || !_host.UseSameSpacingInsideAndOutsideGroups)
            return;

        RefreshAffectedGridSurfaces();
        _host.RefreshSpacingEditorState();
    }

    public IconSpacing ResolveEffectiveSpacing(LinkedSpacingSurfaceSnapshot surface)
    {
        var decision = LinkedIconSpacingPolicy.Resolve(new LinkedIconSpacingContext(
            _host.MasterEnabled,
            _host.IsSafeMode,
            _host.CanEditLayout,
            _host.UseSameSpacingInsideAndOutsideGroups,
            surface.SurfaceKind,
            surface.SavedGroupSpacing,
            _host.GetLooseDesktopIconSpacing()));
        return decision.EffectiveSpacing;
    }

    private void RefreshAffectedGridSurfaces()
    {
        foreach (var surface in _host.EnumerateGroupSurfaces())
        {
            if (surface.SurfaceKind == ItemSurfaceLayoutKind.Grid)
                _host.RefreshGridSurface(surface.SurfaceId);
        }
    }
}
