namespace CyanDeck.Appearance;

/// <summary>
/// Small event-driven FLIP coordinator. The host captures anchors immediately before a known relayout,
/// performs its normal model/layout mutation, then calls Complete after WPF has arranged the final layout.
/// The coordinator never owns a timer, changes model coordinates, or forces UpdateLayout.
/// </summary>
internal sealed class IconMovementAnimationCoordinator
{
    private readonly Action<IconMovementAnimationPlan> _applyPlan;
    private IReadOnlyList<IconMovementAnchor>? _pendingBefore;
    private IconMovementAnimationContext _pendingContext;
    private HashSet<string>? _pendingExcludedItemIds;
    private long _generation;

    internal IconMovementAnimationCoordinator(Action<IconMovementAnimationPlan> applyPlan)
    {
        _applyPlan = applyPlan ?? throw new ArgumentNullException(nameof(applyPlan));
    }

    internal long Begin(
        IReadOnlyList<IconMovementAnchor> before,
        IconMovementAnimationContext context,
        IEnumerable<string>? excludedItemIds = null)
    {
        Cancel();
        _pendingBefore = before?.ToArray() ?? Array.Empty<IconMovementAnchor>();
        _pendingContext = context;
        _pendingExcludedItemIds = excludedItemIds is null
            ? null
            : new HashSet<string>(excludedItemIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        return _generation;
    }

    internal IconMovementAnimationPlan Complete(
        long token,
        IReadOnlyList<IconMovementAnchor> after,
        IconMovementAnimationOptions options,
        bool systemAnimationsEnabled)
    {
        if (token != _generation || _pendingBefore is null)
        {
            return IconMovementAnimationPlan.Suppressed(
                IconMovementAnimationSuppressionReason.InvalidSnapshot,
                options.Normalize().DurationMs);
        }

        IReadOnlyList<IconMovementAnchor> before = _pendingBefore;
        IconMovementAnimationContext context = _pendingContext;
        HashSet<string>? excluded = _pendingExcludedItemIds;
        ClearPendingWithoutAdvancingGeneration();

        IconMovementAnimationPlan plan = IconMovementAnimationPlanner.Build(
            before,
            after ?? Array.Empty<IconMovementAnchor>(),
            context,
            options,
            systemAnimationsEnabled,
            excluded);

        if (plan.ShouldAnimate)
        {
            _applyPlan(plan);
        }

        return plan;
    }

    internal void Cancel()
    {
        _generation++;
        ClearPendingWithoutAdvancingGeneration();
    }

    private void ClearPendingWithoutAdvancingGeneration()
    {
        _pendingBefore = null;
        _pendingExcludedItemIds = null;
        _pendingContext = default;
    }
}
