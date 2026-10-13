using System;
using System.Collections.Generic;
using System.Linq;

namespace CyanDeck.FolderPortals;

public static class PortalViewIconSizePolicy
{
    private static readonly PortalIconSizePreset[] DefaultPresets =
    {
        new(24, "24 px"),
        new(32, "32 px"),
        new(48, "48 px"),
        new(64, "64 px"),
        new(96, "96 px"),
        new(128, "128 px"),
    };

    public static IReadOnlyList<PortalIconSizeMenuItem> BuildMenu(
        PortalIconSizeContext context,
        PortalIconSizeMenuOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalized = (options ?? new PortalIconSizeMenuOptions()).Normalize();
        var presets = NormalizePresets(normalized);
        var enabled = context.IsFolderPortal &&
                      !context.IsSafeMode &&
                      context.MasterEnabled &&
                      context.CanEditAppearance;

        var items = presets
            .Select(p => new PortalIconSizeMenuItem(
                p.Label,
                p.Pixels,
                IsChecked: context.CurrentPixels == p.Pixels,
                IsEnabled: enabled))
            .ToList();

        if (presets.All(p => p.Pixels != context.CurrentPixels) &&
            context.CurrentPixels >= normalized.MinimumPixels &&
            context.CurrentPixels <= normalized.MaximumPixels)
        {
            items.Insert(0, new PortalIconSizeMenuItem(
                $"Custom ({context.CurrentPixels} px)",
                Pixels: null,
                IsChecked: true,
                IsEnabled: false,
                IsCustomMarker: true));
        }

        return items;
    }

    public static bool CanApply(
        PortalIconSizeContext context,
        int requestedPixels,
        PortalIconSizeMenuOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalized = (options ?? new PortalIconSizeMenuOptions()).Normalize();
        if (!context.IsFolderPortal || context.IsSafeMode || !context.MasterEnabled || !context.CanEditAppearance)
        {
            return false;
        }

        return NormalizePresets(normalized).Any(p => p.Pixels == requestedPixels);
    }

    private static IReadOnlyList<PortalIconSizePreset> NormalizePresets(PortalIconSizeMenuOptions options)
    {
        var source = options.Presets is { Count: > 0 } ? options.Presets : DefaultPresets;
        return source
            .Where(p => p is not null)
            .Where(p => p.Pixels >= options.MinimumPixels && p.Pixels <= options.MaximumPixels)
            .GroupBy(p => p.Pixels)
            .Select(g => g.First())
            .OrderBy(p => p.Pixels)
            .Select(p => p with { Label = string.IsNullOrWhiteSpace(p.Label) ? $"{p.Pixels} px" : p.Label.Trim() })
            .ToArray();
    }
}
