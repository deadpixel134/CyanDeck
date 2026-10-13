using System.Runtime.InteropServices;
using CyanDeck.Models;

namespace CyanDeck.Services;

public static class RuleEngine
{
    public static DeckTab? PickTab(DeckState state, string path, bool includeRules = true)
    {
        if (includeRules)
        {
            var ruleMatch = PickRuleTab(state, path);
            if (ruleMatch is not null) return ruleMatch;
        }

        var collectionTabs = state.Fences.SelectMany(f => f.Tabs).Where(t => t.Kind == DeckTabKind.Collection).ToArray();
        foreach (var title in DesktopFileCategoryClassifier.PreferredTabTitles(path))
        {
            var match = collectionTabs.FirstOrDefault(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return collectionTabs.FirstOrDefault(t => string.Equals(t.Title, "기타", StringComparison.OrdinalIgnoreCase))
            ?? collectionTabs.FirstOrDefault(t => string.Equals(t.Title, "Other", StringComparison.OrdinalIgnoreCase));
    }


    public static DeckTab? PickRuleTab(DeckState state, string path)
    {
        var tabs = state.Fences.SelectMany((f, fenceIndex) => f.Tabs.Select((t, tabIndex) => new { Tab = t, FenceIndex = fenceIndex, TabIndex = tabIndex }))
            .Where(x => x.Tab.Kind == DeckTabKind.Collection && x.Tab.Rules.Any(r => r.Enabled))
            .OrderByDescending(x => x.Tab.RulePriority)
            .ThenBy(x => x.FenceIndex)
            .ThenBy(x => x.TabIndex);

        foreach (var x in tabs)
        {
            var active = x.Tab.Rules.Where(r => r.Enabled).ToArray();
            if (active.Length == 0) continue;
            var match = x.Tab.RuleMatchMode == DeckRuleMatchMode.All
                ? active.All(r => Matches(r, path))
                : active.Any(r => Matches(r, path));
            if (match) return x.Tab;
        }
        return null;
    }

    public static bool Matches(DeckRule rule, string path)
    {
        if (!rule.Enabled) return false;
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        return rule.Field switch
        {
            RuleField.Extension => rule.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(p => string.Equals(NormalizeExt(p), ext, StringComparison.OrdinalIgnoreCase)),
            RuleField.NameContains => HasPattern(rule.Pattern) && name.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            RuleField.NameDoesNotContain => HasPattern(rule.Pattern) && !name.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            RuleField.NameExact => HasPattern(rule.Pattern) && string.Equals(name, rule.Pattern, StringComparison.OrdinalIgnoreCase),
            RuleField.NameStartsWith => HasPattern(rule.Pattern) && name.StartsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            RuleField.NameEndsWith => HasPattern(rule.Pattern) && name.EndsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            RuleField.PathContains => HasPattern(rule.Pattern) && path.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            RuleField.Folder => Directory.Exists(path),
            RuleField.Shortcut => IsShortcut(ext),
            RuleField.Executable => string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".msi", StringComparison.OrdinalIgnoreCase),
            RuleField.FileSizeAtLeastMB => FileSizeMatches(path, rule.Pattern, atLeast: true),
            RuleField.FileSizeAtMostMB => FileSizeMatches(path, rule.Pattern, atLeast: false),
            RuleField.ModifiedWithinDays => IsWithinDays(path, rule.Pattern, creation: false),
            RuleField.CreatedWithinDays => IsWithinDays(path, rule.Pattern, creation: true),
            RuleField.CreatedDayOfWeek => CreatedDayMatches(path, rule.Pattern),
            RuleField.CreatedTimeRange => CreatedTimeMatches(path, rule.Pattern),
            RuleField.ShortcutTargetContains => ShortcutTargetMatches(path, rule.Pattern, StringMatch.Contains),
            RuleField.ShortcutTargetDoesNotContain => ShortcutTargetMatches(path, rule.Pattern, StringMatch.NotContains),
            RuleField.ShortcutTargetStartsWith => ShortcutTargetMatches(path, rule.Pattern, StringMatch.StartsWith),
            RuleField.ShortcutTargetEndsWith => ShortcutTargetMatches(path, rule.Pattern, StringMatch.EndsWith),
            RuleField.IconTypeIs => IconTypeMatches(path, rule.Pattern, negate: false),
            RuleField.IconTypeIsNot => IconTypeMatches(path, rule.Pattern, negate: true),
            RuleField.FileCategoryIs => DesktopFileCategoryClassifier.MatchesToken(path, rule.Pattern),
            RuleField.FileCategoryIsNot => !DesktopFileCategoryClassifier.MatchesToken(path, rule.Pattern),
            _ => false
        };
    }

    public static string DescribeRule(DeckRule rule) => rule.Field switch
    {
        RuleField.Extension => $"확장자: {rule.Pattern}",
        RuleField.NameContains => $"이름에 '{rule.Pattern}' 포함",
        RuleField.NameDoesNotContain => $"이름에 '{rule.Pattern}' 포함하지 않음",
        RuleField.NameExact => $"이름이 '{rule.Pattern}'와 정확히 일치",
        RuleField.NameStartsWith => $"이름이 '{rule.Pattern}'로 시작",
        RuleField.NameEndsWith => $"이름이 '{rule.Pattern}'로 끝남",
        RuleField.PathContains => $"경로에 '{rule.Pattern}' 포함",
        RuleField.Folder => "폴더",
        RuleField.Shortcut => "바로 가기",
        RuleField.Executable => "실행 파일/설치 파일",
        RuleField.FileSizeAtLeastMB => $"파일 크기 {rule.Pattern} MB 이상",
        RuleField.FileSizeAtMostMB => $"파일 크기 {rule.Pattern} MB 이하",
        RuleField.ModifiedWithinDays => $"최근 {rule.Pattern}일 이내 수정",
        RuleField.CreatedWithinDays => $"최근 {rule.Pattern}일 이내 생성",
        RuleField.CreatedDayOfWeek => $"생성 요일: {rule.Pattern}",
        RuleField.CreatedTimeRange => $"생성 시간: {rule.Pattern}",
        RuleField.ShortcutTargetContains => $"바로 가기 대상에 '{rule.Pattern}' 포함",
        RuleField.ShortcutTargetDoesNotContain => $"바로 가기 대상에 '{rule.Pattern}' 포함하지 않음",
        RuleField.ShortcutTargetStartsWith => $"바로 가기 대상이 '{rule.Pattern}'로 시작",
        RuleField.ShortcutTargetEndsWith => $"바로 가기 대상이 '{rule.Pattern}'로 끝남",
        RuleField.IconTypeIs => $"아이콘 형식: {rule.Pattern}",
        RuleField.IconTypeIsNot => $"아이콘 형식 제외: {rule.Pattern}",
        RuleField.FileCategoryIs => $"파일 분류: {rule.Pattern}",
        RuleField.FileCategoryIsNot => $"파일 분류 제외: {rule.Pattern}",
        _ => rule.Field.ToString()
    };

    public static string Category(string path)
    {
        return DesktopFileCategoryClassifier.PreferredTabTitles(path).FirstOrDefault() ?? "기타";
    }

    public static IEnumerable<string> EnumerateDesktopItems()
    {
        foreach (var dir in DesktopDirectoryService.GetRoots())
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToArray(); }
            catch { continue; }
            foreach (var p in entries) yield return p;
        }
    }

    private static bool IconTypeMatches(string path, string pattern, bool negate)
    {
        if (!HasPattern(pattern)) return false;
        var actual = ClassifyIconType(path);
        var expected = NormalizeIconTypeToken(pattern);
        if (expected is null) return false;
        var equal = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return negate ? !equal : equal;
    }

    private static string ClassifyIconType(string path)
    {
        try
        {
            if (Directory.Exists(path)) return "folder";
        }
        catch { }
        var ext = Path.GetExtension(path);
        if (IsShortcut(ext)) return "shortcut";
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".msix", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)) return "application";
        return string.IsNullOrWhiteSpace(path) ? "other" : "file";
    }

    private static string? NormalizeIconTypeToken(string value)
    {
        var token = value.Trim().ToLowerInvariant();
        return token switch
        {
            "file" or "regularfile" or "파일" => "file",
            "folder" or "directory" or "폴더" => "folder",
            "shortcut" or "link" or "shortcutlink" or "shortcut (link)" or "바로가기" => "shortcut",
            "application" or "app" or "executable" or "앱" or "실행파일" => "application",
            "other" or "shell" or "shellitem" or "기타" => "other",
            _ => null
        };
    }

    private enum StringMatch { Contains, NotContains, StartsWith, EndsWith }

    private static bool HasPattern(string pattern) => !string.IsNullOrWhiteSpace(pattern);

    private static bool ShortcutTargetMatches(string path, string pattern, StringMatch mode)
    {
        if (!HasPattern(pattern)) return false;
        var target = GetShortcutTarget(path);
        if (string.IsNullOrWhiteSpace(target)) return false;
        return mode switch
        {
            StringMatch.Contains => target.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            StringMatch.NotContains => !target.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            StringMatch.StartsWith => target.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            StringMatch.EndsWith => target.EndsWith(pattern, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool FileSizeMatches(string path, string pattern, bool atLeast)
    {
        if (!double.TryParse(pattern, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) || mb < 0)
            return false;
        try
        {
            if (!File.Exists(path)) return false;
            var sizeMb = new FileInfo(path).Length / (1024d * 1024d);
            return atLeast ? sizeMb >= mb : sizeMb <= mb;
        }
        catch { return false; }
    }

    private static bool CreatedDayMatches(string path, string pattern)
    {
        if (!HasPattern(pattern)) return false;
        DateTime created;
        try { created = File.GetCreationTime(path); }
        catch { return false; }
        var tokens = pattern.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (Enum.TryParse<DayOfWeek>(token, true, out var day) && day == created.DayOfWeek) return true;
            if (int.TryParse(token, out var n))
            {
                // Accept both 0-6 (Sunday=0) and ISO-like 1-7 (Monday=1, Sunday=7).
                if (n is >= 0 and <= 6 && (int)created.DayOfWeek == n) return true;
                var iso = created.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)created.DayOfWeek;
                if (n is >= 1 and <= 7 && iso == n) return true;
            }
        }
        return false;
    }

    private static bool CreatedTimeMatches(string path, string pattern)
    {
        if (!HasPattern(pattern)) return false;
        var parts = pattern.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TimeOnly.TryParse(parts[0], out var start) || !TimeOnly.TryParse(parts[1], out var end)) return false;
        DateTime created;
        try { created = File.GetCreationTime(path); }
        catch { return false; }
        var time = TimeOnly.FromDateTime(created);
        return start <= end ? time >= start && time <= end : time >= start || time <= end;
    }

    private static bool IsShortcut(string ext) => string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, ".website", StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinDays(string path, string pattern, bool creation)
    {
        if (!double.TryParse(pattern, out var days) || days < 0) return false;
        try
        {
            var timestamp = creation ? File.GetCreationTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            if (timestamp == DateTime.MinValue || timestamp > DateTime.UtcNow.AddMinutes(5)) return false;
            return DateTime.UtcNow - timestamp <= TimeSpan.FromDays(days);
        }
        catch { return false; }
    }

    private static string? GetShortcutTarget(string path)
    {
        try
        {
            var ext = Path.GetExtension(path);
            if (string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".website", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line[4..].Trim();
                }
                return null;
            }

            if (!string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell is null) return null;
                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object?[] { path });
                if (shortcut is null) return null;
                return shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            }
            finally
            {
                if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        catch { return null; }
    }

    private static string NormalizeExt(string value) => value.StartsWith('.') ? value : "." + value;
}
