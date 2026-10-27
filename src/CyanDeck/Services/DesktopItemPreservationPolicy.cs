namespace CyanDeck.Services;

/// <summary>
/// Pure decisions that keep desktop icons and their group placement from disappearing or
/// silently moving back. No WPF, file-system or state dependency so the compiled harness
/// can exercise every branch.
/// </summary>
internal static class DesktopItemPreservationPolicy
{
    /// <summary>Removing at least this many desktop references in one pass saves a recovery checkpoint first.</summary>
    internal const int MassRemovalCheckpointThreshold = 10;

    /// <summary>
    /// A freshly created file (for example Explorer's "새 폴더" that is renamed right away) may still
    /// follow the rules after its rename; an established item stays in the tab it is in.
    /// </summary>
    internal static readonly TimeSpan RenamePinMinimumAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Automatic arrangement leaves an item that is already in a group where it is when the user
    /// placed it there, or when continuous rule maintenance is off. Only an explicit rules run
    /// (forceRules) reclassifies it.
    /// </summary>
    internal static bool KeepsExistingGroupPlacement(bool forceRules, bool useRules, bool isGrouped, bool anyUserPlaced)
        => isGrouped && !forceRules && (anyUserPlaced || !useRules);

    /// <summary>A renamed item keeps its tab unless it was only just created.</summary>
    internal static bool ShouldPinRenamedItem(DateTime addedUtc, DateTime nowUtc)
        => nowUtc - addedUtc >= RenamePinMinimumAge;

    internal static bool NeedsCheckpointBeforeRemoval(int removalCount)
        => removalCount >= MassRemovalCheckpointThreshold;

    /// <summary>
    /// True when a previously watched desktop root is missing and no new root replaced it. This
    /// is what a network/USB/other-drive desktop looks like while it is briefly unreachable
    /// (resume from sleep, reconnect), so it must not be treated as "moved or deleted".
    /// </summary>
    internal static bool IsRootSetShrink(IReadOnlyCollection<string> boundRoots, IReadOnlyCollection<string> currentRoots)
        => boundRoots.Count > 0
           && boundRoots.Any(root => !currentRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
           && currentRoots.All(root => boundRoots.Contains(root, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Skip an automatic reconcile only while a watched root is unreachable and the shrink gate is
    /// still actively observing it. An explicit rules run is never skipped (missing items are
    /// protected by the readable-folder checks anyway), and a gate nobody feeds any more (location
    /// tracking off) cannot block reconciles forever.
    /// </summary>
    internal static bool ShouldSkipReconcile(bool forceRules, bool shrinkActivelyPending,
        IReadOnlyCollection<string> boundRoots, IReadOnlyCollection<string> currentRoots)
        => !forceRules && shrinkActivelyPending && IsRootSetShrink(boundRoots, currentRoots);

    /// <summary>
    /// Programs that save through a temp file rename the original away and then put a file with
    /// the original name back (Word: report.docx -> ~WRL0001.tmp). A grouped reference follows a
    /// rename only when the old name is really gone and the new name really exists.
    /// </summary>
    internal static bool ShouldApplyGroupedRename(bool oldPathPresent, bool newPathPresent)
        => !oldPathPresent && newPathPresent;

    /// <summary>At most one "before-desktop-cleanup" checkpoint per hour so routine deletions do not push out others.</summary>
    internal static readonly TimeSpan DesktopCleanupCheckpointInterval = TimeSpan.FromHours(1);

    internal static bool ShouldSaveDesktopCleanupCheckpoint(int removalCount, DateTime lastSavedUtc, DateTime nowUtc)
        => NeedsCheckpointBeforeRemoval(removalCount)
           && (nowUtc < lastSavedUtc || nowUtc - lastSavedUtc >= DesktopCleanupCheckpointInterval);

    /// <summary>
    /// A missing reference may only be removed when its folder could actually be read in this
    /// pass. An unreadable folder means "unknown", never "deleted".
    /// </summary>
    internal static bool CanJudgeMissing(bool parentReadable, bool parentEnumerationFailed)
        => parentReadable && !parentEnumerationFailed;
}

/// <summary>
/// Accepts a smaller desktop root set only after it was observed several times over a minimum
/// period. A brief disconnect therefore never rebinds watchers or migrates stored paths.
/// Called on the UI thread only.
/// </summary>
internal sealed class DesktopRootShrinkGate
{
    internal static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(20);
    // The shell timer checks every 4 seconds. A longer silence means the folder came back in
    // between (the caller does not ask while the roots match), so counting starts over.
    internal static readonly TimeSpan MaximumObservationGap = TimeSpan.FromSeconds(12);
    internal const int MinimumObservations = 3;

    private string? _pendingKey;
    private DateTime _firstSeenUtc;
    private DateTime _lastSeenUtc;
    private int _observations;

    internal bool IsPending => _pendingKey is not null;

    /// <summary>Pending and observed recently; a gate nobody feeds any more stops counting as pending.</summary>
    internal bool IsActivelyPending(DateTime nowUtc)
        => _pendingKey is not null && nowUtc >= _lastSeenUtc && nowUtc - _lastSeenUtc <= MaximumObservationGap;

    internal bool ShouldAccept(IReadOnlyCollection<string> boundRoots, IReadOnlyCollection<string> currentRoots, DateTime nowUtc)
    {
        if (!DesktopItemPreservationPolicy.IsRootSetShrink(boundRoots, currentRoots))
        {
            Reset();
            return true;
        }

        var key = string.Join("|", currentRoots
            .Select(root => root.ToUpperInvariant())
            .OrderBy(root => root, StringComparer.Ordinal));
        if (!string.Equals(key, _pendingKey, StringComparison.Ordinal)
            || nowUtc - _lastSeenUtc > MaximumObservationGap
            || nowUtc < _lastSeenUtc)
        {
            _pendingKey = key;
            _firstSeenUtc = nowUtc;
            _lastSeenUtc = nowUtc;
            _observations = 1;
            return false;
        }

        _lastSeenUtc = nowUtc;
        _observations++;
        if (_observations >= MinimumObservations && nowUtc - _firstSeenUtc >= MinimumDuration)
        {
            Reset();
            return true;
        }
        return false;
    }

    internal void Reset()
    {
        _pendingKey = null;
        _firstSeenUtc = default;
        _lastSeenUtc = default;
        _observations = 0;
    }
}
