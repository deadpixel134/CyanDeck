using System;

namespace CyanDeck.Automation;

public static class ExactNameRuleMatcher
{
    public static ExactNameRuleEvaluation Evaluate(
        in ExactNameRuleCondition condition,
        in AutomationExactNameFacts facts)
    {
        if (!ExactNameRuleValuePolicy.IsValid(condition.ExpectedName))
        {
            return new(ExactNameRuleStatus.InvalidRuleValue, false);
        }

        var actual = facts.EffectiveName ?? string.Empty;
        var matches = string.Equals(
            actual,
            condition.ExpectedName,
            StringComparison.OrdinalIgnoreCase);

        return new(
            matches ? ExactNameRuleStatus.Matched : ExactNameRuleStatus.NotMatched,
            matches);
    }
}
