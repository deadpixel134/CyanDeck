using System;
using System.Collections.Generic;

namespace DesktopDeck.FolderPortals;

public sealed record PortalIconSizePreset(int Pixels, string Label);

public sealed record PortalIconSizeMenuOptions
{
    public int MinimumPixels { get; init; } = 24;
    public int MaximumPixels { get; init; } = 128;
    public IReadOnlyList<PortalIconSizePreset>? Presets { get; init; }

    public PortalIconSizeMenuOptions Normalize()
    {
        var min = Math.Clamp(MinimumPixels, 16, 512);
        var max = Math.Clamp(MaximumPixels, min, 512);
        return this with { MinimumPixels = min, MaximumPixels = max };
    }
}

public sealed record PortalIconSizeMenuItem(
    string Label,
    int? Pixels,
    bool IsChecked,
    bool IsEnabled,
    bool IsCustomMarker = false);

public sealed record PortalIconSizeContext(
    bool IsFolderPortal,
    bool IsSafeMode,
    bool MasterEnabled,
    bool CanEditAppearance,
    int CurrentPixels);
