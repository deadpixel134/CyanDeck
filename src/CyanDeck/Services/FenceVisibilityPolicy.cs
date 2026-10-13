namespace CyanDeck.Services;

/// <summary>
/// Pure visibility policy for a CyanDeck group. This is separate from
/// WPF Show/Hide and desktop shell attachment so it can be tested without
/// opening windows or altering the Explorer desktop state.
/// </summary>
internal static class FenceVisibilityPolicy
{
    internal static bool ShouldShow(
        int windowPage,
        bool showOnAllPages,
        bool alwaysVisible,
        int currentPage,
        bool quickHidden,
        bool quickHideEnabled,
        bool quickHideTargetsOnlyUngroupedIcons)
    {
        var pageVisible = showOnAllPages || windowPage == currentPage;
        var quickVisible = !quickHidden || !quickHideEnabled || quickHideTargetsOnlyUngroupedIcons || alwaysVisible;
        return pageVisible && quickVisible;
    }
}
