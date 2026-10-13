using System;
using System.Collections.Generic;
using System.Linq;

namespace CyanDeck.QuickHide;

/// <summary>
/// Computes single/multi-selection context-menu state without mutating model or visibility.
/// </summary>
public static class LooseIconQuickHideContextMenuPolicy
{
    public static LooseIconBatchMenuState GetMenuState(
        IEnumerable<LooseDesktopItemQuickHideSnapshot>? selection,
        bool editCapabilityAllowed)
    {
        if (selection is null)
            return Hidden();

        var eligible = selection
            .Where(x => x is not null && x.IsEditableManagedLooseIcon)
            .ToArray();

        if (eligible.Length == 0)
            return Hidden();

        var excluded = eligible.Count(x => x.ExcludeFromQuickHide);
        var check = excluded switch
        {
            0 => LooseIconBatchCheckState.Unchecked,
            var n when n == eligible.Length => LooseIconBatchCheckState.Checked,
            _ => LooseIconBatchCheckState.Indeterminate
        };

        // Mixed selection behaves like common Windows batch checkboxes: selecting the command
        // makes all eligible items excluded; invoking again when all are checked clears them.
        var nextValue = excluded != eligible.Length;
        return new LooseIconBatchMenuState(
            check,
            eligible.Length,
            nextValue,
            editCapabilityAllowed);
    }

    public static bool CanShowForSingleItem(LooseDesktopItemQuickHideSnapshot? item)
        => item is not null && item.IsEditableManagedLooseIcon;

    private static LooseIconBatchMenuState Hidden()
        => new(LooseIconBatchCheckState.Hidden, 0, NextExcludeValue: false, IsEnabled: false);
}
