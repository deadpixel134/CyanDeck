namespace CyanDeck.Services;

/// <summary>
/// Folder Portal change notifications arrive in bursts (downloads, archive extraction,
/// build output). Collect them and refresh once after a short quiet period, but never
/// postpone a refresh by more than <see cref="MaximumDelay"/> after the first pending change.
/// Pure timing logic so it can be verified without a dispatcher.
/// </summary>
internal static class FolderPortalRefreshCoalescingPolicy
{
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(2);

    /// <summary>Time left until the pending refresh should run.</summary>
    internal static TimeSpan NextDelay(DateTime firstPendingUtc, DateTime lastChangeUtc, DateTime nowUtc)
    {
        var quietDue = lastChangeUtc + QuietPeriod;
        var capDue = firstPendingUtc + MaximumDelay;
        var due = quietDue < capDue ? quietDue : capDue;
        var delay = due - nowUtc;
        if (delay < TimeSpan.Zero) return TimeSpan.Zero;
        return delay > MaximumDelay ? MaximumDelay : delay;
    }
}
