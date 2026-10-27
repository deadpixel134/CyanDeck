namespace CyanDeck.Services;

/// <summary>
/// Parses the "created on weekday" rule value. Accepts Korean (월, 월요일), English
/// (Monday, Mon) and numbers (0-6 with Sunday=0, or ISO 1-7 with Sunday=7), separated by
/// commas or semicolons. Pure logic so it can be tested without the file system.
/// </summary>
internal static class RuleWeekdayParser
{
    private static readonly Dictionary<string, DayOfWeek> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["일"] = DayOfWeek.Sunday, ["일요일"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday,
        ["월"] = DayOfWeek.Monday, ["월요일"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday,
        ["화"] = DayOfWeek.Tuesday, ["화요일"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday,
        ["수"] = DayOfWeek.Wednesday, ["수요일"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday,
        ["목"] = DayOfWeek.Thursday, ["목요일"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday,
        ["금"] = DayOfWeek.Friday, ["금요일"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday,
        ["토"] = DayOfWeek.Saturday, ["토요일"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday,
    };

    internal static bool Matches(string? pattern, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var tokens = pattern.Split(new[] { ';', ',', '·', '/' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (int.TryParse(token, out var n))
            {
                // Accept both 0-6 (Sunday=0) and ISO-like 1-7 (Monday=1, Sunday=7).
                if (n is >= 0 and <= 6 && (int)day == n) return true;
                var iso = day == DayOfWeek.Sunday ? 7 : (int)day;
                if (n is >= 1 and <= 7 && iso == n) return true;
                continue;
            }
            if (TryParseName(token, out var named) && named == day) return true;
        }
        return false;
    }

    internal static bool TryParseName(string? token, out DayOfWeek day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var value = token.Trim().TrimEnd('.');
        if (Names.TryGetValue(value, out day)) return true;
        // Full English names ("Monday"); numbers are handled by the caller.
        return !value.All(char.IsDigit) && Enum.TryParse(value, true, out day) && Enum.IsDefined(day);
    }
}
