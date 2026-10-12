using System;
using System.Collections.Generic;

namespace DesktopDeck.Automation;

/// <summary>
/// Explicit allow-list normalization for persisted setting IDs. No reflection or display-name lookup.
/// </summary>
public static class DefaultDesktopPlacementSettingsAdapter
{
    public static DefaultDesktopPlacementOptions Normalize(
        DefaultDesktopPlacementKind kind,
        string? groupId,
        string? tabId,
        bool activateDestinationTab,
        IReadOnlySet<string> eligibleCollectionTargetKeys)
    {
        ArgumentNullException.ThrowIfNull(eligibleCollectionTargetKeys);

        if (kind != DefaultDesktopPlacementKind.CollectionTab)
            return DefaultDesktopPlacementOptions.Defaults with { ActivateDestinationTab = false };

        var target = new CollectionTabTarget(groupId ?? string.Empty, tabId ?? string.Empty);
        var key = DefaultDesktopPlacementPlanner.BuildTargetKey(target);
        if (string.IsNullOrEmpty(key) || !eligibleCollectionTargetKeys.Contains(key))
            return DefaultDesktopPlacementOptions.Defaults;

        return new DefaultDesktopPlacementOptions(
            new DefaultDesktopPlacementTarget(DefaultDesktopPlacementKind.CollectionTab, target.GroupId, target.TabId),
            activateDestinationTab);
    }
}
