using System;

namespace DesktopDeck.Automation;

public static class DefaultDesktopPlacementPlanner
{
    public static DesktopPlacementDecision Plan(DesktopPlacementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentNullException.ThrowIfNull(request.Options.Target);
        ArgumentNullException.ThrowIfNull(request.EligibleCollectionTargetKeys);

        if (!request.IsNewlyObserved)
            return new(DesktopPlacementDecisionKind.IgnoreNotNew);

        if (!request.IsManagedDesktopReference)
            return new(DesktopPlacementDecisionKind.IgnoreUnmanaged);

        if (!request.AutoImportEnabled)
            return new(DesktopPlacementDecisionKind.IgnoreAutoImportDisabled);

        if (request.RuleState == RulePlacementState.ValidCollectionTarget)
        {
            if (IsEligible(request.RuleTarget, request.EligibleCollectionTargetKeys))
                return new(DesktopPlacementDecisionKind.RouteByRule, request.RuleTarget, ActivateDestinationTab: false);

            return new(DesktopPlacementDecisionKind.LeaveOnDesktopInvalidRuleTarget);
        }

        if (request.RuleState == RulePlacementState.MatchedButInvalidTarget)
            return new(DesktopPlacementDecisionKind.LeaveOnDesktopInvalidRuleTarget);

        if (request.Options.Target.Kind == DefaultDesktopPlacementKind.Desktop)
            return new(DesktopPlacementDecisionKind.LeaveOnDesktop);

        var defaultTarget = request.Options.Target.AsCollectionTarget();
        if (!IsEligible(defaultTarget, request.EligibleCollectionTargetKeys))
            return new(DesktopPlacementDecisionKind.LeaveOnDesktopInvalidDefaultTarget);

        return new(
            DesktopPlacementDecisionKind.RouteToDefaultTarget,
            defaultTarget,
            request.Options.ActivateDestinationTab);
    }

    public static string BuildTargetKey(CollectionTabTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsValid)
            return string.Empty;

        return target.GroupId + "\u001f" + target.TabId;
    }

    private static bool IsEligible(CollectionTabTarget? target, System.Collections.Generic.IReadOnlySet<string> eligible)
    {
        if (target is null || !target.IsValid)
            return false;

        return eligible.Contains(BuildTargetKey(target));
    }
}
