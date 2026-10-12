namespace DesktopDeck.Usage;

internal enum OpenCountSortDirection
{
    Ascending,
    Descending,
}

internal readonly record struct OpenCountEntry(string ItemId, long Count);

internal readonly record struct OpenDispatchOutcome(string ItemId, bool Succeeded);

internal sealed class OpenCountStatisticsState
{
    public Dictionary<string, long> ItemOpenCounts { get; init; } = new(StringComparer.Ordinal);
}
