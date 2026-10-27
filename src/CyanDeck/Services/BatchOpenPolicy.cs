namespace CyanDeck.Services;

internal enum BatchOpenDecision
{
    /// <summary>Open every selected item without asking.</summary>
    OpenAll,
    /// <summary>Ask "open all N?" first; open every item only on Yes.</summary>
    ConfirmThenOpenAll,
    /// <summary>Large-batch confirmation is off: keep the safety cap and tell the user.</summary>
    OpenFirstAndNotify,
}

internal readonly record struct BatchOpenPlan(BatchOpenDecision Decision, int OpenCount, int TotalCount);

/// <summary>
/// One rule for opening several group items at once (Enter, the CyanDeck batch menu and
/// the Windows menu "open" verb). Selections at or above the large-batch threshold reuse
/// the existing large-batch confirmation setting instead of silently dropping items; when
/// that confirmation is turned off the historical 12-item cap is kept and announced.
/// Pure logic so the rule can be verified without WPF.
/// </summary>
internal static class BatchOpenPolicy
{
    internal const int UnconfirmedOpenLimit = 12;

    internal static BatchOpenPlan Plan(int totalCount, bool confirmLargeBatches, int largeBatchThreshold)
    {
        var total = Math.Max(0, totalCount);
        // Same clamp as the stored setting and the delete confirmation. The user's threshold
        // is honoured first, even below the 12-item cap (threshold 5, 10 items -> ask).
        var threshold = Math.Clamp(largeBatchThreshold, 5, 500);
        if (confirmLargeBatches && total >= threshold)
            return new BatchOpenPlan(BatchOpenDecision.ConfirmThenOpenAll, total, total);
        if (total <= UnconfirmedOpenLimit)
            return new BatchOpenPlan(BatchOpenDecision.OpenAll, total, total);
        if (!confirmLargeBatches)
            return new BatchOpenPlan(BatchOpenDecision.OpenFirstAndNotify, UnconfirmedOpenLimit, total);
        return new BatchOpenPlan(BatchOpenDecision.OpenAll, total, total);
    }
}
