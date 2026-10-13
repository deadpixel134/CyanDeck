namespace CyanDeck.Services;

/// <summary>
/// Prevent incidental pointer movement from selecting a different group tab.
/// Click selection remains handled by the tab's Button.Click event; hover selection
/// is an explicit opt-in and is never performed while dragging.
/// </summary>
internal static class TabSwitchActivationPolicy
{
    internal static bool ShouldSelectOnPointerEnter(
        bool switchTabsOnHover, bool alreadySelected, bool leftButtonPressed, bool tabDragInProgress)
        => switchTabsOnHover && !alreadySelected && !leftButtonPressed && !tabDragInProgress;
}
