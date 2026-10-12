using System.Windows;
using System.Windows.Input;

namespace DesktopDeck.Input;

internal static class WpfCreationGestureAdapter
{
    internal static DesktopCreationMouseButton Map(MouseButton button) =>
        button switch
        {
            MouseButton.Left => DesktopCreationMouseButton.Left,
            MouseButton.Right => DesktopCreationMouseButton.Right,
            _ => DesktopCreationMouseButton.Other,
        };

    // A normal Explorer context-menu click must win over the optional rectangle-creation
    // gesture. The raw system drag threshold can be only a few DIPs, which is easy to cross
    // through ordinary mouse jitter before RBUTTONUP. Require a deliberate 12-DIP drag before
    // DesktopDeck takes ownership; the press/release itself remains completely unconsumed.
    internal const double MinimumCreationDragDistanceDip = 12d;

    internal static CreationMouseButtonOptions WithSystemDragThresholds(CreationMouseButtonMode mode) =>
        new CreationMouseButtonOptions(
            mode,
            Math.Max(SystemParameters.MinimumHorizontalDragDistance, MinimumCreationDragDistanceDip),
            Math.Max(SystemParameters.MinimumVerticalDragDistance, MinimumCreationDragDistanceDip)).Normalize();
}
