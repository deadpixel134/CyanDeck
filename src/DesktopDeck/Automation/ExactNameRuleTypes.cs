namespace DesktopDeck.Automation;

/// <summary>
/// Name facts are supplied by DesktopDeck's existing item metadata/rule pipeline.
/// Use the same effective name that the current name contains/starts/ends rules see.
/// </summary>
public readonly record struct AutomationExactNameFacts(string? EffectiveName);

public readonly record struct ExactNameRuleCondition(string? ExpectedName);

public enum ExactNameRuleStatus
{
    Matched,
    NotMatched,
    InvalidRuleValue
}

public readonly record struct ExactNameRuleEvaluation(
    ExactNameRuleStatus Status,
    bool Matches);
