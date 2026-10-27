using System.Windows;
using CyanDeck.Services;

namespace CyanDeck.Views;

/// <summary>
/// Shared access to the dark theme styles for code-built UI (calendar panel, context menus)
/// that does not merge the theme dictionaries into its own resources. Only keyed styles are
/// handed out, so implicit styles never leak into windows that did not ask for them.
/// A dictionary that cannot be loaded is reported once and callers keep their plain look.
/// </summary>
internal static class DeckThemeResources
{
    private const string ControlsUri = "/CyanDeck;component/Themes/DarkControls.xaml";
    private const string MenuUri = "/CyanDeck;component/Themes/DeckMenu.xaml";
    private const string ComboBoxesUri = "/CyanDeck;component/Themes/ReadableComboBoxes.xaml";
    private static readonly Dictionary<string, ResourceDictionary?> Loaded = new(StringComparer.Ordinal);

    internal static Style? ControlStyle(string key) => Find(ControlsUri, key);

    /// <summary>
    /// Gives a small dialog the shared dark controls (buttons, text boxes, check boxes) with the
    /// readable native combo boxes the Settings window uses. Returns false and leaves the window
    /// unchanged if the theme cannot be loaded.
    /// </summary>
    internal static bool MergeDarkControls(ResourceDictionary target)
    {
        try
        {
            target.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri(ControlsUri, UriKind.Relative)));
            target.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri(ComboBoxesUri, UriKind.Relative)));
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Dark controls could not be merged into a dialog", ex);
            return false;
        }
    }

    internal static Style? MenuStyle(object key) => Find(MenuUri, key);

    private static Style? Find(string uri, object key)
    {
        if (!Loaded.TryGetValue(uri, out var dictionary))
        {
            try
            {
                dictionary = (ResourceDictionary)Application.LoadComponent(new Uri(uri, UriKind.Relative));
            }
            catch (Exception ex)
            {
                dictionary = null;
                DiagnosticsLog.Error("Theme dictionary could not be loaded: " + uri, ex);
            }
            Loaded[uri] = dictionary;
        }
        if (dictionary is null || !dictionary.Contains(key)) return null;
        return dictionary[key] as Style;
    }
}
