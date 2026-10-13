using System;

namespace CyanDeck.Automation;

public static class IconTypeRuleIntegrationContract
{
    public const string SettingCapability = "Automation.EditRules";
    public const string ConditionDisplayName = "Icon type";
    public const string IsOperatorDisplayName = "is";
    public const string IsNotOperatorDisplayName = "is not";

    // These are integration markers, not assumptions about CyanDeck's
    // existing numeric condition enum. Append new condition kinds to the host's
    // serialized enum/list rather than renumbering any existing values.
    public const string ConditionKindIs = "IconTypeIs";
    public const string ConditionKindIsNot = "IconTypeIsNot";

    public static bool IsSupportedConditionKind(string? kind) =>
        kind == ConditionKindIs || kind == ConditionKindIsNot;

    public static IconTypeRuleOperator ToOperator(string kind) => kind switch
    {
        ConditionKindIs => IconTypeRuleOperator.Is,
        ConditionKindIsNot => IconTypeRuleOperator.IsNot,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported icon-type condition kind.")
    };
}
