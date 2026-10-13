namespace CyanDeck.Appearance;

/// <summary>
/// Pure policy for Fences-6-style titlebar controls on unselected tabs.
/// It performs no persistence, timers, input capture, model mutation, or window discovery.
/// </summary>
public static class InactiveTabTitlebarControlPolicy
{
    public static InactiveTabTitlebarControlDecision Evaluate(
        InactiveTabTitlebarControlOptions? options,
        InactiveTabTitlebarControlContext context)
    {
        // Selected tabs keep the existing v1.9 behavior exactly as-is.
        if (context.IsTabSelected)
        {
            return new(InactiveTabTitlebarControlDecisionKind.DeferToExistingSelectedTabPolicy);
        }

        options ??= new InactiveTabTitlebarControlOptions();

        if (!context.IsHeaderPresented || !context.IsGroupInteractive)
        {
            return new(InactiveTabTitlebarControlDecisionKind.Hidden);
        }

        if (!options.ShowControlsInInactiveTabs)
        {
            return new(InactiveTabTitlebarControlDecisionKind.Hidden);
        }

        // Existing global behavior: when mouseover-only is disabled, controls are persistent.
        if (!options.ShowControlsOnMouseover)
        {
            return new(InactiveTabTitlebarControlDecisionKind.Visible);
        }

        if (context.IsPointerOverHeader)
        {
            return new(InactiveTabTitlebarControlDecisionKind.Visible);
        }

        if (options.KeyboardFocusRevealsControls && context.IsKeyboardFocusWithinHeader)
        {
            return new(InactiveTabTitlebarControlDecisionKind.Visible);
        }

        return new(InactiveTabTitlebarControlDecisionKind.Hidden);
    }
}
