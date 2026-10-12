using System.Collections.ObjectModel;

namespace DesktopDeck.Appearance;

internal enum IconMovementAnimationContext
{
    SortOrArrange,
    ResizeCompleted,
    ItemSetChanged,
    ExternalRefresh,
    DragCommit,
    ManualArrange,
    InitialMaterialization,
    ViewModeChanged,
    SearchFilterChanged,
    PageTransition,
    SnapshotRestore,
    DisplayRecovery,
}

internal enum IconMovementAnimationSuppressionReason
{
    None,
    Disabled,
    SystemAnimationsDisabled,
    ContextSuppressed,
    InvalidSnapshot,
    TooManyMoves,
    NoMoves,
}

internal readonly record struct IconMovementAnchor(
    string SurfaceId,
    string ItemId,
    double XDip,
    double YDip,
    bool IsRealized = true);

internal readonly record struct IconMovementVector(
    string SurfaceId,
    string ItemId,
    double FromOffsetXDip,
    double FromOffsetYDip,
    double DistanceDip);

internal sealed record IconMovementAnimationPlan(
    IconMovementAnimationSuppressionReason SuppressionReason,
    IReadOnlyList<IconMovementVector> Moves,
    int DurationMs)
{
    internal bool ShouldAnimate =>
        SuppressionReason == IconMovementAnimationSuppressionReason.None && Moves.Count > 0;

    internal static IconMovementAnimationPlan Suppressed(
        IconMovementAnimationSuppressionReason reason,
        int durationMs) =>
        new(reason, Array.Empty<IconMovementVector>(), durationMs);

    internal static IconMovementAnimationPlan Create(
        List<IconMovementVector> moves,
        int durationMs) =>
        new(
            IconMovementAnimationSuppressionReason.None,
            new ReadOnlyCollection<IconMovementVector>(moves),
            durationMs);
}

internal readonly record struct IconMovementAnimationOptions(
    bool Enabled,
    int DurationMs,
    int MaxAnimatedItems,
    double MinimumDistanceDip,
    double MaximumDistanceDip,
    bool RespectSystemAnimationSetting)
{
    internal const int MinimumDurationMs = 80;
    internal const int MaximumDurationMs = 300;
    internal const int MinimumBatchSize = 8;
    internal const int MaximumBatchSize = 256;
    internal const double MinimumAllowedDistanceDip = 0.25;
    internal const double MaximumAllowedDistanceDip = 4096.0;

    // Migration-safe: pre-v7.18 DesktopDeck did not perform per-icon FLIP movement animation.
    internal static IconMovementAnimationOptions Default => new(
        Enabled: false,
        DurationMs: 160,
        MaxAnimatedItems: 128,
        MinimumDistanceDip: 1.0,
        MaximumDistanceDip: 1600.0,
        RespectSystemAnimationSetting: true);

    internal IconMovementAnimationOptions Normalize() => new(
        Enabled,
        Math.Clamp(DurationMs, MinimumDurationMs, MaximumDurationMs),
        Math.Clamp(MaxAnimatedItems, MinimumBatchSize, MaximumBatchSize),
        Math.Clamp(
            double.IsFinite(MinimumDistanceDip) ? MinimumDistanceDip : 1.0,
            MinimumAllowedDistanceDip,
            64.0),
        Math.Clamp(
            double.IsFinite(MaximumDistanceDip) ? MaximumDistanceDip : 1600.0,
            64.0,
            MaximumAllowedDistanceDip),
        RespectSystemAnimationSetting);
}
