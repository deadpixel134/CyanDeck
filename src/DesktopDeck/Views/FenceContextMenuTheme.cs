using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopDeck.Views;

/// <summary>
/// Owns fence context-menu visual theming and open-menu accounting.
/// Shell recovery must not reorder layered windows while any such menu is open.
/// </summary>
internal static class FenceContextMenuTheme
{
    private static int _openContextMenuCount;
    internal static bool IsAnyMenuOpen => Volatile.Read(ref _openContextMenuCount) > 0;

    internal static ContextMenu Create()
    {
        // ContextMenu opens in its own Popup/HWND and does not reliably inherit implicit
        // Window.Resources from the Fence visual tree. Seed both direct properties and the
        // system brush keys consumed by the stock WPF MenuItem template so text cannot fall
        // back to gray-on-gray under Windows light/dark/high-contrast combinations.
        // Keep Deck menus deliberately simple and readable. The previous custom dark palette
        // interacted poorly with stock submenu Popup templates on some Windows themes.
        // Outside high-contrast mode use a conventional white menu with black text.
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

        var menu = new ContextMenu
        {
            Background = background,
            Foreground = foreground,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
        };
        SeedMenuBrushResources(menu.Resources, background, foreground, disabled, highlight, highlightText);

        // Submenus are rendered by separate Popup HWNDs. Do not depend on the root ContextMenu's
        // visual inheritance: give every MenuItem an implicit style whose direct colors survive
        // the popup boundary. This prevents Windows theme defaults from producing gray-on-gray
        // submenu text/background combinations.
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

        var countedOpen = false;
        menu.Opened += (_, _) =>
        {
            ApplyMenuThemeRecursively(menu.Items, itemStyle, background, foreground, disabled, highlight, highlightText);
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

    private static void ApplyMenuThemeRecursively(ItemCollection items, Style itemStyle, Brush background, Brush foreground, Brush disabled, Brush highlight, Brush highlightText)
    {
        foreach (var entry in items)
        {
            if (entry is not MenuItem item) continue;
            item.Style = itemStyle;
            SeedMenuBrushResources(item.Resources, background, foreground, disabled, highlight, highlightText);
            if (item.Items.Count > 0)
                ApplyMenuThemeRecursively(item.Items, itemStyle, background, foreground, disabled, highlight, highlightText);
        }
    }

}
