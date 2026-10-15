using System;
using System.Collections.Generic;
using System.Linq;

namespace CyanDeck.Layout;

/// <summary>
/// Rejects malformed saved display placements before any live Fence model or
/// WPF window is mutated. Does not depend on Win32 or user data storage.
/// </summary>
internal static class SavedMonitorLayoutSafetyPolicy
{
    internal readonly record struct Geometry(Guid Id, double X, double Y,
        double Width, double Height, double ExpandedHeight);

    internal static bool IsUsable(IReadOnlyList<Geometry>? stored)
    {
        if (stored is null || stored.Count == 0) return false;
        var ids = new HashSet<Guid>();
        foreach (var geometry in stored)
        {
            if (geometry.Id == Guid.Empty || !ids.Add(geometry.Id) ||
                !double.IsFinite(geometry.X) || !double.IsFinite(geometry.Y) ||
                !double.IsFinite(geometry.Width) || !double.IsFinite(geometry.Height) ||
                !double.IsFinite(geometry.ExpandedHeight) ||
                geometry.Width <= 0 || geometry.Height <= 0 ||
                geometry.ExpandedHeight <= 0)
                return false;
        }
        return true;
    }

    // A change in the observed topology between settling and the actual
    // restore callback must NOT be treated as a stable display configuration.
    internal static bool MatchesSettledTopology(string? settledSignature,
        int settledCount, string? observedSignature, int observedCount) =>
        settledCount > 0 && settledCount == observedCount &&
        !string.IsNullOrWhiteSpace(settledSignature) &&
        string.Equals(settledSignature, observedSignature, StringComparison.Ordinal);
}
