namespace DesktopDeck.Usage;

/// <summary>
/// Tracks successful user-initiated opens by DesktopDeck stable item id.
/// No path, filename, target, timestamp, process name, or Shell identity is stored here.
/// </summary>
internal sealed class ItemOpenCountTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

    internal ItemOpenCountTracker(OpenCountStatisticsState? initialState = null)
    {
        if (initialState is null)
        {
            return;
        }

        foreach ((string itemId, long count) in initialState.ItemOpenCounts)
        {
            if (IsValidItemId(itemId) && count > 0)
            {
                _counts[itemId] = count;
            }
        }
    }

    internal long GetCount(string? itemId)
    {
        if (!IsValidItemId(itemId))
        {
            return 0;
        }

        lock (_gate)
        {
            return _counts.TryGetValue(itemId!, out long count) ? count : 0;
        }
    }

    internal long RecordSuccessfulOpen(string itemId)
    {
        if (!IsValidItemId(itemId))
        {
            throw new ArgumentException("A stable DesktopDeck item id is required.", nameof(itemId));
        }

        lock (_gate)
        {
            _counts.TryGetValue(itemId, out long current);
            long next = current == long.MaxValue ? long.MaxValue : current + 1;
            _counts[itemId] = next;
            return next;
        }
    }

    internal int RecordSuccessfulBatch(IEnumerable<OpenDispatchOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        int increments = 0;
        foreach (OpenDispatchOutcome outcome in outcomes)
        {
            if (!outcome.Succeeded || !IsValidItemId(outcome.ItemId))
            {
                continue;
            }

            RecordSuccessfulOpen(outcome.ItemId);
            increments++;
        }

        return increments;
    }

    internal bool RemoveIdentity(string itemId)
    {
        if (!IsValidItemId(itemId))
        {
            return false;
        }

        lock (_gate)
        {
            return _counts.Remove(itemId);
        }
    }

    internal OpenCountStatisticsState ExportState()
    {
        lock (_gate)
        {
            var copy = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach ((string itemId, long count) in _counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (count > 0)
                {
                    copy[itemId] = count;
                }
            }

            return new OpenCountStatisticsState { ItemOpenCounts = copy };
        }
    }

    internal int NonZeroIdentityCount
    {
        get
        {
            lock (_gate)
            {
                return _counts.Count;
            }
        }
    }

    private static bool IsValidItemId(string? itemId) => !string.IsNullOrWhiteSpace(itemId);
}
