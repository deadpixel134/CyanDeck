using System;

namespace CyanDeck.Automation;

public interface IContinuousAutomationHost
{
    bool MasterEnabled { get; }
    bool IsSafeMode { get; }
    bool KeepRulesUpdatedAtAllTimes { get; set; }
    bool CanApplyAutomationRules { get; }

    void ApplyAutomationRules(AutomationUpdateTrigger trigger);
    void RefreshAutomationUi();
    void SaveState();
    void LogAutomationDecision(AutomationUpdateTrigger trigger, AutomationUpdateDecisionCode code);
}

public sealed class ContinuousAutomationCoordinator
{
    private readonly IContinuousAutomationHost _host;

    public ContinuousAutomationCoordinator(IContinuousAutomationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public AutomationUpdateDecision Handle(AutomationUpdateTrigger trigger)
    {
        var context = new AutomationUpdateContext(
            _host.KeepRulesUpdatedAtAllTimes,
            _host.MasterEnabled,
            _host.IsSafeMode,
            _host.CanApplyAutomationRules);

        var decision = ContinuousAutomationPolicy.Decide(trigger, context);
        _host.LogAutomationDecision(trigger, decision.Code);

        if (decision.AllowMoves)
            _host.ApplyAutomationRules(trigger);

        return decision;
    }

    public ContinuousAutomationSettingResult SetKeepRulesUpdatedAtAllTimes(bool enabled)
    {
        if (_host.KeepRulesUpdatedAtAllTimes == enabled)
            return ContinuousAutomationSettingResult.Unchanged;

        var previous = _host.KeepRulesUpdatedAtAllTimes;
        _host.KeepRulesUpdatedAtAllTimes = enabled;
        _host.RefreshAutomationUi();

        try
        {
            // Changing this setting never runs the rules immediately. The user
            // has a separate, explicit Apply rules now action for a one-shot pass.
            _host.SaveState();
            return ContinuousAutomationSettingResult.Saved;
        }
        catch
        {
            _host.KeepRulesUpdatedAtAllTimes = previous;
            _host.RefreshAutomationUi();
            return ContinuousAutomationSettingResult.SaveFailedRolledBack;
        }
    }
}
