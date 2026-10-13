using System;
using System.Windows.Controls;

namespace CyanDeck.FolderPortals;

/// <summary>
/// Populates an existing Folder Portal View submenu. It does not own or replace the host context menu.
/// </summary>
public sealed class WpfPortalViewIconSizeMenuBridge
{
    private readonly PortalViewIconSizeCoordinator _coordinator;

    public WpfPortalViewIconSizeMenuBridge(PortalViewIconSizeCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public MenuItem BuildIconSizeSubmenu()
    {
        var root = new MenuItem { Header = "Icon size" };
        foreach (var item in _coordinator.BuildMenu())
        {
            var menuItem = new MenuItem
            {
                Header = item.Label,
                IsCheckable = true,
                IsChecked = item.IsChecked,
                IsEnabled = item.IsEnabled,
                StaysOpenOnClick = false,
                Tag = item.Pixels,
            };

            if (item.Pixels is int pixels)
            {
                menuItem.Click += (_, _) => _coordinator.TryApply(pixels);
            }

            root.Items.Add(menuItem);
        }

        root.IsEnabled = root.Items.Count > 0;
        return root;
    }
}
