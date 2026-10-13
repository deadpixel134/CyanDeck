namespace CyanDeck.Services;

/// <summary>
/// Shared, extension-based desktop classification used by automatic routing and rule matching.
/// Category labels are stable tokens; RuleEngine maps them to Korean/legacy English tab names.
/// </summary>
public static class DesktopFileCategoryClassifier
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic", ".heif", ".avif", ".tif", ".tiff", ".svg", ".ico" };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".wmv", ".mpeg", ".mpg", ".ts" };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff" };

    private static readonly HashSet<string> Pdf = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };
    private static readonly HashSet<string> Spreadsheets = new(StringComparer.OrdinalIgnoreCase)
    { ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".csv", ".tsv" };
    private static readonly HashSet<string> Presentations = new(StringComparer.OrdinalIgnoreCase)
    { ".ppt", ".pptx", ".pptm", ".odp", ".key" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    { ".doc", ".docx", ".docm", ".odt", ".rtf", ".hwp", ".hwpx", ".pages" };
    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".markdown", ".log", ".nfo", ".ini", ".cfg", ".conf" };
    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".xaml", ".xml", ".json", ".yaml", ".yml", ".toml", ".js", ".ts", ".tsx", ".jsx", ".html", ".htm", ".css", ".scss", ".py", ".java", ".kt", ".kts", ".cpp", ".c", ".h", ".hpp", ".rs", ".go", ".php", ".rb", ".sql", ".ps1", ".sh" };
    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".iso" };
    private static readonly HashSet<string> Installers = new(StringComparer.OrdinalIgnoreCase)
    { ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller" };
    private static readonly HashSet<string> Apps = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".com", ".bat", ".cmd", ".appref-ms" };
    private static readonly HashSet<string> Shortcuts = new(StringComparer.OrdinalIgnoreCase)
    { ".lnk", ".url", ".website" };

    public static string Classify(string path)
    {
        try { if (Directory.Exists(path)) return "folder"; } catch { }
        var ext = Path.GetExtension(path);
        if (Shortcuts.Contains(ext) || Apps.Contains(ext)) return "app";
        if (Installers.Contains(ext)) return "installer";
        if (Pdf.Contains(ext)) return "pdf";
        if (Spreadsheets.Contains(ext)) return "spreadsheet";
        if (Presentations.Contains(ext)) return "presentation";
        if (Documents.Contains(ext)) return "document";
        if (Text.Contains(ext)) return "text";
        if (Code.Contains(ext)) return "code";
        if (Images.Contains(ext)) return "image";
        if (Videos.Contains(ext)) return "video";
        if (Audio.Contains(ext)) return "audio";
        if (Archives.Contains(ext)) return "archive";
        return "other";
    }

    public static IReadOnlyList<string> PreferredTabTitles(string path)
    {
        return Classify(path) switch
        {
            "folder" => ["폴더", "Folders"],
            "app" => ["앱", "Apps"],
            "installer" => ["설치", "설치 파일", "Apps"],
            "pdf" => ["PDF", "문서", "Documents"],
            "spreadsheet" => ["시트", "스프레드시트", "문서", "Documents"],
            "presentation" => ["발표", "프레젠테이션", "문서", "Documents"],
            "document" => ["문서", "Documents"],
            "text" => ["텍스트", "문서", "Documents"],
            "code" => ["코드", "문서", "Documents", "기타", "Other"],
            "image" => ["이미지", "Images"],
            "video" => ["동영상", "Media"],
            "audio" => ["오디오", "Media"],
            "archive" => ["압축", "압축 파일", "Archives"],
            _ => ["기타", "Other"],
        };
    }

    public static bool MatchesToken(string path, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var actual = Classify(path);
        foreach (var raw in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = NormalizeToken(raw);
            if (token == "media" && actual is "image" or "video" or "audio") return true;
            if (token == "office" && actual is "pdf" or "spreadsheet" or "presentation" or "document" or "text") return true;
            if (string.Equals(actual, token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string NormalizeToken(string value)
    {
        var token = value.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        return token switch
        {
            "폴더" or "folder" or "directory" => "folder",
            "앱" or "application" or "applications" or "app" or "apps" or "shortcut" or "shortcuts" => "app",
            "설치" or "설치파일" or "installer" or "installers" or "setup" => "installer",
            "pdf" => "pdf",
            "시트" or "스프레드시트" or "spreadsheet" or "spreadsheets" or "sheet" => "spreadsheet",
            "발표" or "프레젠테이션" or "presentation" or "presentations" or "slide" or "slides" => "presentation",
            "문서" or "document" or "documents" or "doc" => "document",
            "텍스트" or "text" or "note" or "notes" => "text",
            "코드" or "code" or "source" => "code",
            "이미지" or "사진" or "image" or "images" or "photo" or "photos" => "image",
            "동영상" or "비디오" or "video" or "videos" => "video",
            "오디오" or "음악" or "audio" or "music" => "audio",
            "미디어" or "media" => "media",
            "오피스" or "office" => "office",
            "압축" or "압축파일" or "archive" or "archives" or "compressed" => "archive",
            "기타" or "other" or "others" => "other",
            _ => token,
        };
    }
}
