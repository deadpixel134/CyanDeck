namespace CyanDeck.Usage;

/// <summary>
/// Records usage only after CyanDeck's existing open command reports success.
/// The overlay never launches files, resolves shortcuts, or invokes Shell verbs itself.
/// </summary>
internal sealed class OpenCountCommandBridge
{
    private readonly ItemOpenCountTracker _tracker;
    private readonly Action? _requestPersist;

    internal OpenCountCommandBridge(ItemOpenCountTracker tracker, Action? requestPersist)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _requestPersist = requestPersist;
    }

    internal bool RecordAfterDispatch(string itemId, bool dispatchSucceeded)
    {
        if (!dispatchSucceeded)
        {
            return false;
        }

        _tracker.RecordSuccessfulOpen(itemId);
        _requestPersist?.Invoke();
        return true;
    }

    internal int RecordBatchAfterDispatch(IEnumerable<OpenDispatchOutcome> outcomes)
    {
        int changed = _tracker.RecordSuccessfulBatch(outcomes);
        if (changed > 0)
        {
            _requestPersist?.Invoke();
        }

        return changed;
    }

    internal async ValueTask<bool> DispatchAndRecordAsync(
        string itemId,
        Func<CancellationToken, ValueTask<bool>> existingOpenCommand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingOpenCommand);

        bool succeeded = await existingOpenCommand(cancellationToken).ConfigureAwait(false);
        return RecordAfterDispatch(itemId, succeeded);
    }
}
