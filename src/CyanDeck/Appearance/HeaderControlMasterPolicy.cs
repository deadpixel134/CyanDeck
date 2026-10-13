namespace CyanDeck.Appearance;

/// <summary>
/// Top-level gate for title/header controls. This policy never decides whether a specific
/// selected/inactive control should be visible; v1.9 and v7.8 retain that responsibility.
/// </summary>
internal static class HeaderControlMasterPolicy
{
    internal static HeaderControlMasterDecision Decide(
        HeaderControlMasterContext context,
        HeaderControlMasterOptions options)
    {
        if (!context.IsHeaderPresented ||
            !context.IsGroupInteractive ||
            context.DeferToHostPresentation)
        {
            return HeaderControlMasterDecision.DeferToExistingPolicy;
        }

        return options.ShowControlsInGroupHeaders
            ? HeaderControlMasterDecision.DeferToExistingPolicy
            : HeaderControlMasterDecision.HideAllControls;
    }
}
