using System;

namespace DesktopDeck.Automation;

public static class DesktopIconTypeClassifier
{
    public static DesktopIconType Classify(in AutomationIconTypeFacts facts)
    {
        // Virtual/non-filesystem shell items are intentionally kept outside the
        // normal file/folder rules. The host can expose them as Other without
        // probing or mutating Explorer namespace state.
        if (!facts.IsFileSystemItem)
            return DesktopIconType.Other;

        if (facts.IsDirectory)
            return DesktopIconType.Folder;

        var extension = NormalizeExtension(facts.Extension);

        // Explicit metadata wins. The extension aliases are migration/fallback
        // support for item metadata implementations that do not yet expose a
        // dedicated shortcut flag.
        if (facts.IsShortcut || IsShortcutExtension(extension))
            return DesktopIconType.ShortcutLink;

        if (facts.IsApplication || IsApplicationExtension(extension))
            return DesktopIconType.Application;

        return DesktopIconType.File;
    }

    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var trimmed = extension.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        if (trimmed[0] != '.')
            trimmed = "." + trimmed;

        return trimmed.ToLowerInvariant();
    }

    private static bool IsShortcutExtension(string extension) =>
        extension is ".lnk" or ".url" or ".website";

    private static bool IsApplicationExtension(string extension) =>
        extension is ".exe" or ".com" or ".bat" or ".cmd" or
                     ".msi" or ".msix" or ".appref-ms";
}
