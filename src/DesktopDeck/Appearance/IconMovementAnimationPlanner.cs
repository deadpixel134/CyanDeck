namespace DesktopDeck.Appearance;

internal static class IconMovementAnimationPlanner
{
    internal static IconMovementAnimationPlan Build(
        IReadOnlyList<IconMovementAnchor> before,
        IReadOnlyList<IconMovementAnchor> after,
        IconMovementAnimationContext context,
        IconMovementAnimationOptions rawOptions,
        bool systemAnimationsEnabled,
        IReadOnlySet<string>? excludedItemIds = null)
    {
        IconMovementAnimationOptions options = rawOptions.Normalize();

        if (!options.Enabled)
        {
            return IconMovementAnimationPlan.Suppressed(
                IconMovementAnimationSuppressionReason.Disabled,
                options.DurationMs);
        }

        if (options.RespectSystemAnimationSetting && !systemAnimationsEnabled)
        {
            return IconMovementAnimationPlan.Suppressed(
                IconMovementAnimationSuppressionReason.SystemAnimationsDisabled,
                options.DurationMs);
        }

        if (IsSuppressedContext(context))
        {
            return IconMovementAnimationPlan.Suppressed(
                IconMovementAnimationSuppressionReason.ContextSuppressed,
                options.DurationMs);
        }

        if (before is null || after is null)
        {
            return IconMovementAnimationPlan.Suppressed(
                IconMovementAnimationSuppressionReason.InvalidSnapshot,
                options.DurationMs);
        }

        Dictionary<(string SurfaceId, string ItemId), IconMovementAnchor> previous = new();
        foreach (IconMovementAnchor anchor in before)
        {
            if (!IsUsable(anchor))
            {
                continue;
            }

            // Duplicate stable IDs on the same surface indicate a broken host snapshot.
            // Keep the first value instead of making animation state nondeterministic.
            previous.TryAdd((anchor.SurfaceId, anchor.ItemId), anchor);
        }

        List<IconMovementVector> moves = new();
        HashSet<(string SurfaceId, string ItemId)> seenAfter = new();

        foreach (IconMovementAnchor current in after)
        {
            if (!IsUsable(current) ||
                !seenAfter.Add((current.SurfaceId, current.ItemId)) ||
                excludedItemIds?.Contains(current.ItemId) == true ||
                !previous.TryGetValue((current.SurfaceId, current.ItemId), out IconMovementAnchor old))
            {
                continue;
            }

            double dx = old.XDip - current.XDip;
            double dy = old.YDip - current.YDip;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));

            if (!double.IsFinite(distance) ||
                distance < options.MinimumDistanceDip ||
                distance > options.MaximumDistanceDip)
            {
                continue;
            }

            moves.Add(new IconMovementVector(
                current.SurfaceId,
                current.ItemId,
                dx,
                dy,
                distance));

            // Suppress the entire batch rather than animating an arbitrary subset.
            // That avoids visual discontinuity and protects the UI thread on very large relayouts.
            if (moves.Count > options.MaxAnimatedItems)
            {
                return IconMovementAnimationPlan.Suppressed(
                    IconMovementAnimationSuppressionReason.TooManyMoves,
                    options.DurationMs);
            }
        }

        return moves.Count == 0
            ? IconMovementAnimationPlan.Suppressed(
                IconMovementAnimationSuppressionReason.NoMoves,
                options.DurationMs)
            : IconMovementAnimationPlan.Create(moves, options.DurationMs);
    }

    internal static bool IsSuppressedContext(IconMovementAnimationContext context) => context is
        IconMovementAnimationContext.InitialMaterialization or
        IconMovementAnimationContext.ViewModeChanged or
        IconMovementAnimationContext.SearchFilterChanged or
        IconMovementAnimationContext.PageTransition or
        IconMovementAnimationContext.SnapshotRestore or
        IconMovementAnimationContext.DisplayRecovery;

    private static bool IsUsable(IconMovementAnchor anchor) =>
        anchor.IsRealized &&
        !string.IsNullOrWhiteSpace(anchor.SurfaceId) &&
        !string.IsNullOrWhiteSpace(anchor.ItemId) &&
        double.IsFinite(anchor.XDip) &&
        double.IsFinite(anchor.YDip);
}
