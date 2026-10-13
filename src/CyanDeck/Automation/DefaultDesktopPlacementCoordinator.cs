using System;

namespace CyanDeck.Automation;

/// <summary>
/// Applies only a previously planned managed-reference move. The host owns rule evaluation,
/// filesystem watching, persistence, and the actual collection mutation transaction.
/// </summary>
public sealed class DefaultDesktopPlacementCoordinator
{
    private readonly Func<CollectionTabTarget, bool> _targetStillExists;
    private readonly Func<string, CollectionTabTarget, bool> _moveManagedReference;
    private readonly Action<CollectionTabTarget>? _activateTab;

    public DefaultDesktopPlacementCoordinator(
        Func<CollectionTabTarget, bool> targetStillExists,
        Func<string, CollectionTabTarget, bool> moveManagedReference,
        Action<CollectionTabTarget>? activateTab = null)
    {
        _targetStillExists = targetStillExists ?? throw new ArgumentNullException(nameof(targetStillExists));
        _moveManagedReference = moveManagedReference ?? throw new ArgumentNullException(nameof(moveManagedReference));
        _activateTab = activateTab;
    }

    public bool TryApply(string managedReferenceId, DesktopPlacementDecision decision)
    {
        if (string.IsNullOrWhiteSpace(managedReferenceId) || decision is null || !decision.MovesReference || decision.Target is null)
            return false;

        // Revalidate at commit time because a tab can be removed after planning.
        if (!_targetStillExists(decision.Target))
            return false;

        if (!_moveManagedReference(managedReferenceId, decision.Target))
            return false;

        if (decision.ActivateDestinationTab)
            _activateTab?.Invoke(decision.Target);

        return true;
    }
}
