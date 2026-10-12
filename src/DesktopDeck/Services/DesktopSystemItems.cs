using System.Diagnostics;
using DesktopDeck.Models;

namespace DesktopDeck.Services;

/// <summary>
/// A small allow-list of Windows' built-in virtual desktop icons. These items are
/// namespace objects, not files; never classify, rename, or delete them as file paths.
/// IDs and shell paths are stable across process restarts and Windows UI languages.
/// </summary>
internal static class DesktopSystemItems
{
    internal const string RecycleBinShellPath = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";
    internal const string RecycleBinExplorerArgument = "shell:RecycleBinFolder";
    internal static readonly Guid RecycleBinItemId = new("645FF040-5081-101B-9F08-00AA002F954E");

    private static readonly (Guid Id, string Path, string Name)[] KnownItems =
    [
        (RecycleBinItemId, RecycleBinShellPath, "휴지통"),
        (new Guid("20D04FE0-3AEA-1069-A2D8-08002B30309D"), "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "내 PC"),
        (new Guid("59031A47-3F72-44A7-89C5-5595FE6B30EE"), "shell:::{59031A47-3F72-44A7-89C5-5595FE6B30EE}", "사용자 폴더"),
        (new Guid("F02C1A0D-BE21-4350-88B0-7367FC96EF3C"), "shell:::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "네트워크"),
        (new Guid("26EE0668-A00A-44D7-9371-BEB064C98683"), "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}", "제어판"),
    ];

    internal static DeckItem CreateRecycleBin(double? x, double? y) => new()
    {
        Id = RecycleBinItemId,
        Path = RecycleBinShellPath,
        DisplayNameOverride = "휴지통",
        // Put the automatic first-time position after ordinary loose items. Once the user
        // moves the icon PositionX/PositionY become the source of truth and are persisted.
        AddedUtc = DateTime.MaxValue,
        PositionX = x is double px && double.IsFinite(px) ? px : null,
        PositionY = y is double py && double.IsFinite(py) ? py : null,
        ExcludeFromQuickHide = true,
    };

    // The group entry is independent of the separately hosted desktop Recycle Bin.
    // Each host owns its own visual and neither item is moved or hidden by the other.
    internal static List<DeckItem> CreateGroupItems(bool includeRecycleBin = true) => KnownItems.Where(k => includeRecycleBin || k.Id != RecycleBinItemId).Select((item, index) => new DeckItem
    {
        Id = item.Id,
        Path = item.Path,
        DisplayNameOverride = item.Name,
        AddedUtc = DateTime.UnixEpoch.AddSeconds(index),
        ExcludeFromQuickHide = true,
    }).ToList();

    internal static bool IsRecycleBin(DeckItem? item)
        => item is not null && (item.Id == RecycleBinItemId || IsRecycleBinPath(item.Path));

    internal static bool IsRecycleBinPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && string.Equals(path.Trim(), RecycleBinShellPath, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupportedVirtualItem(DeckItem? item)
        => item is not null && IsSupportedVirtualPath(item.Path);

    internal static bool IsSupportedVirtualPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && KnownItems.Any(k => string.Equals(k.Path, path.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static bool IsShellNamespacePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.TrimStart().StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    internal static void OpenRecycleBin() => OpenVirtualItem(RecycleBinShellPath);

    internal static void OpenVirtualItem(string path)
    {
        if (!IsSupportedVirtualPath(path))
            throw new ArgumentException("지원하지 않는 Windows 시스템 아이콘입니다.", nameof(path));
        var argument = IsRecycleBinPath(path) ? RecycleBinExplorerArgument : path.Trim();
        Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
    }
}
