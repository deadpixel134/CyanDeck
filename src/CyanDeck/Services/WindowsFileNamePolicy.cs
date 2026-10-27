namespace CyanDeck.Services;

/// <summary>
/// Checks a new file or folder name before renaming on disk and explains, in Korean,
/// why Windows would reject it. Returns null when the name can be used.
/// </summary>
internal static class WindowsFileNamePolicy
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

    internal static string? GetProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "이름을 입력하세요.";
        if (name is "." or "..") return "'.'과 '..'은 폴더 위치를 가리키는 예약된 이름이라 사용할 수 없습니다.";
        if (name.IndexOfAny(InvalidChars) >= 0) return "이름에 \\ / : * ? \" < > | 문자는 사용할 수 없습니다.";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "이름은 마침표(.)나 공백으로 끝날 수 없습니다.";
        var stem = name.Split('.')[0].TrimEnd();
        if (ReservedDeviceNames.Contains(stem)) return $"'{stem}'은(는) Windows가 장치 이름으로 예약해 두어 사용할 수 없습니다.";
        return null;
    }
}
