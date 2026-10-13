namespace CyanDeck.Automation;

public enum DesktopIconType
{
    File,
    Folder,
    ShortcutLink,
    Application,
    Other
}

public enum IconTypeRuleOperator
{
    Is,
    IsNot
}

/// <summary>
/// Host-supplied facts from CyanDeck's already-built desktop item metadata.
/// The v7.19 evaluator deliberately does not touch the filesystem.
/// </summary>
public readonly record struct AutomationIconTypeFacts(
    bool IsFileSystemItem,
    bool IsDirectory,
    bool IsShortcut,
    bool IsApplication,
    string? Extension);

public readonly record struct IconTypeRuleCondition(
    IconTypeRuleOperator Operator,
    DesktopIconType ExpectedType);

public readonly record struct IconTypeRuleEvaluation(
    DesktopIconType ActualType,
    bool Matches);

public readonly record struct IconTypeRuleChoice(
    string PersistedValue,
    DesktopIconType Type,
    string DisplayName);
