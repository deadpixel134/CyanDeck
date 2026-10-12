using System;
using System.Collections.Generic;

namespace DesktopDeck.Automation;

public static class IconTypeRuleMatcher
{
    private static readonly IconTypeRuleChoice[] Choices =
    {
        new("file", DesktopIconType.File, "File"),
        new("folder", DesktopIconType.Folder, "Folder"),
        new("shortcut", DesktopIconType.ShortcutLink, "Shortcut (link)"),
        new("application", DesktopIconType.Application, "Application"),
        new("other", DesktopIconType.Other, "Other / shell item")
    };

    private static readonly IReadOnlyList<IconTypeRuleChoice> ReadOnlyChoices = Array.AsReadOnly(Choices);

    public static IReadOnlyList<IconTypeRuleChoice> GetEditorChoices() => ReadOnlyChoices;

    public static IconTypeRuleEvaluation Evaluate(
        in IconTypeRuleCondition condition,
        in AutomationIconTypeFacts facts)
    {
        var actual = DesktopIconTypeClassifier.Classify(facts);
        var equal = actual == condition.ExpectedType;
        var matches = condition.Operator == IconTypeRuleOperator.Is ? equal : !equal;
        return new(actual, matches);
    }

    public static bool TryParsePersistedValue(string? value, out DesktopIconType type)
    {
        if (value is null)
        {
            type = default;
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "file":
            case "regularfile":
                type = DesktopIconType.File;
                return true;
            case "folder":
            case "directory":
                type = DesktopIconType.Folder;
                return true;
            case "shortcut":
            case "link":
            case "shortcutlink":
            case "shortcut (link)":
                type = DesktopIconType.ShortcutLink;
                return true;
            case "application":
            case "app":
            case "executable":
                type = DesktopIconType.Application;
                return true;
            case "other":
            case "shell":
            case "shellitem":
                type = DesktopIconType.Other;
                return true;
            default:
                type = default;
                return false;
        }
    }

    public static string ToPersistedValue(DesktopIconType type) => type switch
    {
        DesktopIconType.File => "file",
        DesktopIconType.Folder => "folder",
        DesktopIconType.ShortcutLink => "shortcut",
        DesktopIconType.Application => "application",
        DesktopIconType.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
