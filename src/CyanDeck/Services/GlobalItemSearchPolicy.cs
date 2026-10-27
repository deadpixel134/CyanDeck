using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CyanDeck.Services;

internal enum GlobalItemSearchSource { Collection, FolderPortal, Desktop }

/// <summary>One searchable icon: a group tab item, a Folder Portal entry or an icon outside groups.</summary>
internal sealed record GlobalItemSearchEntry(
    string DisplayName,
    string Path,
    GlobalItemSearchSource Source,
    Guid? FenceId,
    Guid? TabId,
    Guid? ItemId,
    string GroupTitle,
    string TabTitle,
    int PageIndex,
    bool ShowOnAllPages)
{
    internal string Location => GlobalItemSearchPolicy.FormatLocation(this);
    internal bool CanReveal => FenceId is not null && TabId is not null;
}

/// <summary>
/// Search across every group, tab, page and the icons outside groups. Matches the shown name
/// and the file name only, never the folder path, so "desktop" or the user name does not
/// match every item. Pure so the ranking and location text are tested.
/// </summary>
internal static class GlobalItemSearchPolicy
{
    internal const int MaxResults = 200;

    internal static string[] Tokenize(string? query)
        => (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    internal static bool Matches(GlobalItemSearchEntry entry, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return false;
        var name = entry.DisplayName ?? "";
        var fileName = SafeFileName(entry.Path);
        foreach (var token in tokens)
        {
            if (!name.Contains(token, StringComparison.CurrentCultureIgnoreCase) &&
                !fileName.Contains(token, StringComparison.CurrentCultureIgnoreCase))
                return false;
        }
        return true;
    }

    internal static IReadOnlyList<GlobalItemSearchEntry> Search(IEnumerable<GlobalItemSearchEntry> entries, string? query, int maxResults = MaxResults)
    {
        var tokens = Tokenize(query);
        if (tokens.Length == 0) return Array.Empty<GlobalItemSearchEntry>();
        var first = tokens[0];
        return entries
            .Where(entry => Matches(entry, tokens))
            .OrderBy(entry => Rank(entry, first))
            .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Source)
            .ThenBy(entry => entry.PageIndex)
            .Take(Math.Max(1, maxResults))
            .ToList();
    }

    // 0: exact name, 1: name starts with the first word, 2: anywhere.
    internal static int Rank(GlobalItemSearchEntry entry, string firstToken)
    {
        var name = entry.DisplayName ?? "";
        if (string.Equals(name, firstToken, StringComparison.CurrentCultureIgnoreCase)) return 0;
        if (name.StartsWith(firstToken, StringComparison.CurrentCultureIgnoreCase)) return 1;
        return 2;
    }

    internal static string FormatLocation(GlobalItemSearchEntry entry)
    {
        if (entry.Source == GlobalItemSearchSource.Desktop) return "바탕화면 (그룹 밖)";
        var group = string.IsNullOrWhiteSpace(entry.GroupTitle) ? "이름 없는 그룹" : entry.GroupTitle.Trim();
        var tab = entry.TabTitle?.Trim() ?? "";
        var place = tab.Length == 0 || string.Equals(tab, group, StringComparison.CurrentCulture)
            ? group
            : group + " › " + tab;
        if (entry.Source == GlobalItemSearchSource.FolderPortal) place += " (폴더 포털)";
        var page = entry.ShowOnAllPages
            ? "모든 페이지"
            : "페이지 " + (Math.Max(0, entry.PageIndex) + 1).ToString(CultureInfo.InvariantCulture);
        return place + " · " + page;
    }

    /// <summary>
    /// In-group search text for a path: an item under one of <paramref name="excludedRoots"/>
    /// (the user's and the public Desktop) matches only by the part after that folder.
    /// </summary>
    internal static string SearchablePath(string? path, IEnumerable<string?> excludedRoots)
    {
        if (string.IsNullOrEmpty(path)) return "";
        foreach (var root in excludedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var trimmed = root.TrimEnd('\\', '/');
            if (trimmed.Length == 0 || path.Length <= trimmed.Length + 1) continue;
            if (!path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)) continue;
            var separator = path[trimmed.Length];
            if (separator == '\\' || separator == '/') return path.Substring(trimmed.Length + 1);
        }
        return path;
    }

    private static string SafeFileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) ?? ""; }
        catch (ArgumentException) { return ""; }
    }
}
