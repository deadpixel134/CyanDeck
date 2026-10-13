using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CyanDeck.QuickHide;

/// <summary>
/// Transaction coordinator for editing per-loose-icon exclusion state.
/// Persistence is delegated to the host so the canonical layout save/audit pipeline remains single-source.
/// </summary>
public sealed class LooseIconQuickHideExclusionCoordinator
{
    private readonly Func<string, LooseDesktopItemQuickHideSnapshot?> _readItem;
    private readonly PersistLooseIconQuickHideMutationsAsync _persistBatchAsync;
    private readonly Action _refreshManagedLooseIconVisibility;
    private readonly Func<bool> _editCapabilityAllowed;

    public LooseIconQuickHideExclusionCoordinator(
        Func<string, LooseDesktopItemQuickHideSnapshot?> readItem,
        PersistLooseIconQuickHideMutationsAsync persistBatchAsync,
        Action refreshManagedLooseIconVisibility,
        Func<bool>? editCapabilityAllowed = null)
    {
        _readItem = readItem ?? throw new ArgumentNullException(nameof(readItem));
        _persistBatchAsync = persistBatchAsync ?? throw new ArgumentNullException(nameof(persistBatchAsync));
        _refreshManagedLooseIconVisibility = refreshManagedLooseIconVisibility
            ?? throw new ArgumentNullException(nameof(refreshManagedLooseIconVisibility));
        _editCapabilityAllowed = editCapabilityAllowed ?? (() => true);
    }

    public async Task<LooseIconExclusionEditResult> SetExcludedAsync(
        IEnumerable<string>? itemIds,
        bool excluded,
        CancellationToken cancellationToken = default)
    {
        if (!_editCapabilityAllowed())
        {
            return new LooseIconExclusionEditResult(
                LooseIconExclusionEditStatus.CapabilityBlocked,
                0,
                VisibilityRefreshRequested: false);
        }

        var requestedIds = (itemIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requestedIds.Length == 0)
        {
            return new LooseIconExclusionEditResult(
                LooseIconExclusionEditStatus.NoEligibleItems,
                0,
                VisibilityRefreshRequested: false);
        }

        // Re-read immediately before planning so a drag/rule move that put the item into a group
        // cannot accidentally persist a loose-icon-only setting through stale context-menu state.
        var current = requestedIds
            .Select(_readItem)
            .Where(x => x is not null)
            .Cast<LooseDesktopItemQuickHideSnapshot>()
            .ToArray();

        if (current.Any(x => !x.HasStableId))
        {
            return new LooseIconExclusionEditResult(
                LooseIconExclusionEditStatus.MissingStableId,
                0,
                VisibilityRefreshRequested: false);
        }

        var eligible = current.Where(x => x.IsEditableManagedLooseIcon).ToArray();
        if (eligible.Length == 0)
        {
            return new LooseIconExclusionEditResult(
                LooseIconExclusionEditStatus.UnsupportedSurface,
                0,
                VisibilityRefreshRequested: false);
        }

        var mutations = eligible
            .Where(x => x.ExcludeFromQuickHide != excluded)
            .Select(x => new LooseIconQuickHideMutation(x.ItemId, excluded))
            .ToArray();

        if (mutations.Length == 0)
        {
            return new LooseIconExclusionEditResult(
                LooseIconExclusionEditStatus.NoChange,
                0,
                VisibilityRefreshRequested: false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool persisted;
        try
        {
            persisted = await _persistBatchAsync(mutations, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            persisted = false;
        }

        if (!persisted)
        {
            return new LooseIconExclusionEditResult(
                LooseIconExclusionEditStatus.PersistFailed,
                0,
                VisibilityRefreshRequested: false);
        }

        // One refresh after one batch save. Do not rebuild the entire desktop or rules engine.
        _refreshManagedLooseIconVisibility();

        return new LooseIconExclusionEditResult(
            LooseIconExclusionEditStatus.Ready,
            mutations.Length,
            VisibilityRefreshRequested: true);
    }
}
