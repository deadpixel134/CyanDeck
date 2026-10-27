using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CyanDeck.Views;

/// <summary>
/// Owns CyanDeck context-menu visual theming and open-menu accounting for group menus.
/// Shell recovery must not reorder layered windows while any such menu is open.
/// </summary>
internal static class FenceContextMenuTheme
{
    private static int _openContextMenuCount;
    internal static bool IsAnyMenuOpen => Volatile.Read(ref _openContextMenuCount) > 0;

    /// <summary>A themed menu for group windows, counted while open.</summary>
    internal static ContextMenu Create()
    {
        var menu = new ContextMenu();
        ApplyTheme(menu);

        var countedOpen = false;
        menu.Opened += (_, _) =>
        {
            if (countedOpen) return;
            countedOpen = true;
            Interlocked.Increment(ref _openContextMenuCount);
        };
        menu.Closed += (_, _) =>
        {
            if (!countedOpen) return;
            countedOpen = false;
            Interlocked.Decrement(ref _openContextMenuCount);
        };
        return menu;
    }

    /// <summary>
    /// Gives any CyanDeck menu (loose desktop icons, calendar events) the same look as the group
    /// menu. High contrast keeps the Windows system colours.
    /// </summary>
    internal static void ApplyTheme(ContextMenu menu)
    {
        if (!TryApplyDeckTheme(menu)) ApplyPlainTheme(menu);
    }

    // Dark CyanDeck menu: the menu, every item role, submenu popups and separators use the
    // templates in Themes/DeckMenu.xaml, so no stock Windows menu template is mixed in.
    private static bool TryApplyDeckTheme(ContextMenu menu)
    {
        if (SystemParameters.HighContrast) return false;
        var menuStyle = DeckThemeResources.MenuStyle("DeckContextMenuStyle");
        var itemStyle = DeckThemeResources.MenuStyle("DeckMenuItemStyle");
        var separatorStyle = DeckThemeResources.MenuStyle(MenuItem.SeparatorStyleKey);
        if (menuStyle is null || itemStyle is null || separatorStyle is null) return false;

        menu.Style = menuStyle;
        menu.Resources[typeof(MenuItem)] = itemStyle;
        menu.Resources[MenuItem.SeparatorStyleKey] = separatorStyle;
        // Submenus open in their own popups: also give every item and separator the style
        // directly, including items a submenu adds while it opens.
        menu.Opened += (_, _) => ApplyDeckStyles(menu.Items, itemStyle, separatorStyle);
        return true;
    }

    private static void ApplyDeckStyles(ItemCollection items, Style itemStyle, Style separatorStyle)
    {
        foreach (var entry in items)
        {
            switch (entry)
            {
                case Separator separator:
                    separator.Style = separatorStyle;
                    break;
                case MenuItem item:
                    item.Style = itemStyle;
                    if (item.Items.Count > 0) ApplyDeckStyles(item.Items, itemStyle, separatorStyle);
                    break;
            }
        }
    }

    // High contrast (or a theme that could not be loaded): a conventional menu on the stock
    // template. Seed both direct properties and the system brush keys consumed by the stock
    // WPF MenuItem template so text cannot fall back to gray-on-gray in submenu popups.
    private static void ApplyPlainTheme(ContextMenu menu)
    {
        Brush background = SystemParameters.HighContrast
            ? SystemColors.MenuBrush
            : Brushes.White;
        Brush foreground = SystemParameters.HighContrast
            ? SystemColors.MenuTextBrush
            : Brushes.Black;
        Brush disabled = SystemParameters.HighContrast
            ? SystemColors.GrayTextBrush
            : new SolidColorBrush(Color.FromRgb(118, 118, 118));
        Brush highlight = SystemParameters.HighContrast
            ? SystemColors.HighlightBrush
            : new SolidColorBrush(Color.FromRgb(229, 241, 251));
        Brush highlightText = SystemParameters.HighContrast
            ? SystemColors.HighlightTextBrush
            : Brushes.Black;
        Brush border = SystemParameters.HighContrast
            ? SystemColors.WindowFrameBrush
            : new SolidColorBrush(Color.FromRgb(204, 204, 204));

        menu.Background = background;
        menu.Foreground = foreground;
        menu.BorderBrush = border;
        menu.BorderThickness = new Thickness(1);
        SeedMenuBrushResources(menu.Resources, background, foreground, disabled, highlight, highlightText);

        var itemStyle = new Style(typeof(MenuItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, background));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        var highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        highlighted.Setters.Add(new Setter(Control.BackgroundProperty, highlight));
        highlighted.Setters.Add(new Setter(Control.ForegroundProperty, highlightText));
        itemStyle.Triggers.Add(highlighted);
        var unavailable = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        unavailable.Setters.Add(new Setter(Control.ForegroundProperty, disabled));
        itemStyle.Triggers.Add(unavailable);
        menu.Resources[typeof(MenuItem)] = itemStyle;

        menu.Opened += (_, _) =>
            ApplyPlainStylesRecursively(menu.Items, itemStyle, background, foreground, disabled, highlight, highlightText);
    }

    private static void SeedMenuBrushResources(ResourceDictionary resources, Brush background, Brush foreground, Brush disabled, Brush highlight, Brush highlightText)
    {
        resources[SystemColors.MenuBrushKey] = background;
        resources[SystemColors.MenuTextBrushKey] = foreground;
        resources[SystemColors.MenuHighlightBrushKey] = highlight;
        resources[SystemColors.HighlightBrushKey] = highlight;
        resources[SystemColors.HighlightTextBrushKey] = highlightText;
        resources[SystemColors.GrayTextBrushKey] = disabled;
        resources[SystemColors.ControlBrushKey] = background;
        resources[SystemColors.ControlTextBrushKey] = foreground;
    }

    private static void ApplyPlainStylesRecursively(ItemCollection items, Style itemStyle, Brush background, Brush foreground, Brush disabled, Brush highlight, Brush highlightText)
    {
        foreach (var entry in items)
        {
            if (entry is not MenuItem item) continue;
            item.Style = itemStyle;
            SeedMenuBrushResources(item.Resources, background, foreground, disabled, highlight, highlightText);
            if (item.Items.Count > 0)
                ApplyPlainStylesRecursively(item.Items, itemStyle, background, foreground, disabled, highlight, highlightText);
        }
    }
}
