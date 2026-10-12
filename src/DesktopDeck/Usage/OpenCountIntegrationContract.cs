namespace DesktopDeck.Usage;

internal static class OpenCountIntegrationContract
{
    internal const string SortDisplayName = "Number of times opened";
    internal const string CanonicalSortKey = "OpenCount";
    internal const string StatePropertyName = "UsageStatistics.ItemOpenCounts";

    internal const string ScopeRule =
        "Expose OpenCount sorting for durable DesktopDeck Collection item references only. " +
        "Do not persist Folder Portal paths solely to synthesize usage identities.";

    internal const string CountRule =
        "Increment exactly once after an existing user-initiated Open command positively reports success; " +
        "selection, preview, reveal-in-Explorer, properties, rename, and failed/cancelled opens do not increment.";

    internal const string PersistenceRule =
        "Persist opaque stable item ids and non-negative counts through DesktopDeck's existing atomic/debounced state writer. " +
        "Layout snapshots and group templates must not restore or clone usage counts.";

    internal const string DuplicationRule =
        "Fresh item ids created by group duplication start at zero. Configuration-id repair may transfer a count only to " +
        "the one canonical surviving identity; newly split duplicate ids start at zero.";

    internal const string NativeMenuRule =
        "Do not guess which arbitrary native IContextMenu verb was invoked. Count native-menu Open only when the host can " +
        "positively identify the Open action and successful dispatch; otherwise leave the count unchanged.";
}
