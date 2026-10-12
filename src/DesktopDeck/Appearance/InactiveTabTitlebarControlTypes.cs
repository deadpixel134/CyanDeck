namespace DesktopDeck.Appearance;

/// <summary>
/// v7.8 only governs titlebar controls that belong to an unselected tab.
/// Selected-tab control visibility remains owned by DesktopDeck's existing v1.9 policy.
/// </summary>
public sealed record InactiveTabTitlebarControlOptions
{
    /// <summary>Migration-safe default: preserve the pre-v7.8 behavior.</summary>
    public bool ShowControlsInInactiveTabs { get; init; } = false;

    /// <summary>
    /// Mirror DesktopDeck's existing "show titlebar controls on mouseover" setting.
    /// When false, eligible inactive-tab controls may remain visible while the header is presented.
    /// </summary>
    public bool ShowControlsOnMouseover { get; init; } = true;

    /// <summary>
    /// Keeps hidden controls discoverable for keyboard users when focus is already within the tab header.
    /// </summary>
    public bool KeyboardFocusRevealsControls { get; init; } = true;
}

public readonly record struct InactiveTabTitlebarControlContext(
    bool IsTabSelected,
    bool IsHeaderPresented,
    bool IsPointerOverHeader,
    bool IsKeyboardFocusWithinHeader,
    bool IsGroupInteractive = true);

public enum InactiveTabTitlebarControlDecisionKind
{
    /// <summary>Do not override the existing selected-tab visibility policy.</summary>
    DeferToExistingSelectedTabPolicy = 0,

    /// <summary>Apply the inactive-tab override and hide/disable the controls.</summary>
    Hidden = 1,

    /// <summary>Apply the inactive-tab override and show/enable the controls.</summary>
    Visible = 2,
}

public readonly record struct InactiveTabTitlebarControlDecision(
    InactiveTabTitlebarControlDecisionKind Kind)
{
    public bool AppliesInactiveOverride =>
        Kind is InactiveTabTitlebarControlDecisionKind.Hidden or
            InactiveTabTitlebarControlDecisionKind.Visible;

    public bool ShowControls => Kind == InactiveTabTitlebarControlDecisionKind.Visible;

    public bool EnableHitTesting => ShowControls;
}
