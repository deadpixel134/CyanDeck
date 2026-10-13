using System;
using System.Collections.Generic;
using System.Linq;

namespace CyanDeck.Services;

/// <summary>
/// Visual-only state for Explorer-style pending move. Source files are not touched
/// until Shell paste succeeds; never install global keyboard or mouse hooks.
/// The WPF dispatcher owns state changes, allowing all open fences to redraw.
/// </summary>
internal static class DeckCutClipboardVisualState
{
    private static HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private static long _version;
    internal static event Action? Changed;
    internal static bool HasCut => _paths.Count != 0;
    internal static string[] Snapshot() => _paths.ToArray();
    internal static long Version => _version;
    internal static bool IsCut(string path) => _paths.Contains(path);

    internal static void Mark(string[] paths)
    {
        var next = paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_paths.SetEquals(next)) return;
        _paths = next;
        _version++;
        Changed?.Invoke();
    }

    internal static void Clear() => Mark([]);
}
