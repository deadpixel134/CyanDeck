using System;
using System.Collections.Generic;

namespace DesktopDeck.Automation;

public enum DefaultDesktopPlacementKind
{
    Desktop = 0,
    CollectionTab = 1,
}

public enum RulePlacementState
{
    NoMatch = 0,
    ValidCollectionTarget = 1,
    MatchedButInvalidTarget = 2,
}

public enum DesktopPlacementDecisionKind
{
    IgnoreNotNew = 0,
    IgnoreUnmanaged = 1,
    IgnoreAutoImportDisabled = 2,
    LeaveOnDesktop = 3,
    RouteByRule = 4,
    RouteToDefaultTarget = 5,
    LeaveOnDesktopInvalidRuleTarget = 6,
    LeaveOnDesktopInvalidDefaultTarget = 7,
}

public sealed record CollectionTabTarget(string GroupId, string TabId)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(GroupId) && !string.IsNullOrWhiteSpace(TabId);
}

public sealed record DefaultDesktopPlacementTarget(
    DefaultDesktopPlacementKind Kind,
    string? GroupId = null,
    string? TabId = null)
{
    public static DefaultDesktopPlacementTarget Desktop { get; } = new(DefaultDesktopPlacementKind.Desktop);

    public CollectionTabTarget? AsCollectionTarget() =>
        Kind == DefaultDesktopPlacementKind.CollectionTab &&
        !string.IsNullOrWhiteSpace(GroupId) &&
        !string.IsNullOrWhiteSpace(TabId)
            ? new CollectionTabTarget(GroupId!, TabId!)
            : null;
}

public sealed record DefaultDesktopPlacementOptions(
    DefaultDesktopPlacementTarget Target,
    bool ActivateDestinationTab = false)
{
    public static DefaultDesktopPlacementOptions Defaults { get; } =
        new(DefaultDesktopPlacementTarget.Desktop, ActivateDestinationTab: false);
}

public sealed record DesktopPlacementRequest(
    string ItemIdentityKey,
    bool IsNewlyObserved,
    bool IsManagedDesktopReference,
    bool AutoImportEnabled,
    RulePlacementState RuleState,
    CollectionTabTarget? RuleTarget,
    DefaultDesktopPlacementOptions Options,
    IReadOnlySet<string> EligibleCollectionTargetKeys);

public sealed record DesktopPlacementDecision(
    DesktopPlacementDecisionKind Kind,
    CollectionTabTarget? Target = null,
    bool ActivateDestinationTab = false)
{
    public bool MovesReference => Kind is DesktopPlacementDecisionKind.RouteByRule or DesktopPlacementDecisionKind.RouteToDefaultTarget;
}
