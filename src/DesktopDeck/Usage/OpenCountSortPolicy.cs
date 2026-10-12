namespace DesktopDeck.Usage;

internal static class OpenCountSortPolicy
{
    internal static int CompareCounts(long left, long right, OpenCountSortDirection direction)
    {
        left = Math.Max(0, left);
        right = Math.Max(0, right);

        int comparison = left.CompareTo(right);
        return direction == OpenCountSortDirection.Descending ? -comparison : comparison;
    }

    /// <summary>
    /// Returns only the primary open-count comparison. A zero result deliberately tells the
    /// existing DesktopDeck sort pipeline to use its normal deterministic secondary/tie-breaker.
    /// </summary>
    internal static int CompareItemIds(
        string leftItemId,
        string rightItemId,
        ItemOpenCountTracker tracker,
        OpenCountSortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        return CompareCounts(tracker.GetCount(leftItemId), tracker.GetCount(rightItemId), direction);
    }
}
