using System;

namespace DesktopDeck.Automation;

public static class ExactNameRuleIntegrationContract
{
    public const string SettingCapability = "Automation.EditRules";
    public const string ConditionDisplayName = "Icon's name is exactly";

    // Integration marker only. Append this condition kind to the host's
    // serialized condition catalog; never renumber existing condition kinds.
    public const string ConditionKind = "NameEquals";

    // Read-only migration aliases. New saves should emit ConditionKind.
    public static bool IsExactNameConditionKind(string? kind) => kind switch
    {
        ConditionKind => true,
        "NameIsExactly" => true,
        "NameExactlyEquals" => true,
        _ => false
    };

    public static string NormalizeConditionKindForNewSave(string kind)
    {
        if (!IsExactNameConditionKind(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported exact-name condition kind.");
        }

        return ConditionKind;
    }
}
