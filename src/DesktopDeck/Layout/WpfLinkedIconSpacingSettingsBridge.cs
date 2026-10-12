namespace DesktopDeck.Layout;

// Thin settings bridge. The canonical Settings view should bind its checkbox to this bridge.
public sealed class WpfLinkedIconSpacingSettingsBridge
{
    private readonly LinkedIconSpacingCoordinator _coordinator;
    private readonly ILinkedIconSpacingHost _host;

    public WpfLinkedIconSpacingSettingsBridge(
        LinkedIconSpacingCoordinator coordinator,
        ILinkedIconSpacingHost host)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public bool IsChecked => _host.UseSameSpacingInsideAndOutsideGroups;
    public bool IsEnabled => _host.MasterEnabled && !_host.IsSafeMode && _host.CanEditLayout;

    public bool Apply(bool value) => _coordinator.TrySetLinked(value);
}
