using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CyanDeck.Appearance;
using CyanDeck.Models;
using CyanDeck.Services;

namespace CyanDeck.Views;

/// <summary>
/// v8.0.43: mouse access to the existing window-local Ctrl+C / Ctrl+X / Ctrl+V file commands
/// (see FenceWindow.Keyboard.cs, whose semantics and Folder Portal path checks are mirrored
/// here) and visible shortcut hints in the group menus. No keyboard hook is involved.
/// </summary>
public partial class FenceWindow
{
    // Adds "복사 Ctrl+C" / "잘라내기 Ctrl+X" for items that exist on disk.
    private void AddClipboardMenuItems(ContextMenu menu, IReadOnlyList<ItemTile> tiles)
    {
        var paths = tiles.Select(tile => tile.Path)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        menu.Items.Add(WithGesture(MakeItem("복사", () => CopyPathsToShellClipboard(paths, cut: false)), "Ctrl+C"));
        menu.Items.Add(WithGesture(MakeItem("잘라내기", () => CopyPathsToShellClipboard(paths, cut: true)), "Ctrl+X"));
    }

    private void CopyPathsToShellClipboard(string[] paths, bool cut)
    {
        DeckShellClipboardService.CopyFiles(paths, cut, Dispatcher,
            error =>
            {
                if (error is not null)
                    MessageBox.Show(this, error, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
                else if (cut) DeckCutClipboardVisualState.Mark(paths);
                else DeckCutClipboardVisualState.Clear();
            });
    }

    // Adds "붙여넣기 Ctrl+V" to the group menu only while the clipboard holds files.
    private void AddPasteMenuItem(ContextMenu menu)
    {
        var tab = SelectedTab;
        if (tab is null || !ClipboardHasFileDrop()) return;
        menu.Items.Add(WithGesture(MakeItem("붙여넣기", () => PasteShellClipboardInto(tab)), "Ctrl+V"));
    }

    private static bool ClipboardHasFileDrop()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch { return false; } // Clipboard busy/locked by another app: just omit the command.
    }

    private void PasteShellClipboardInto(DeckTab tab)
    {
        // Collection tabs are virtual: paste onto the real (possibly redirected) Desktop and
        // include the created paths in this tab, exactly like Ctrl+V.
        var destination = tab.Kind == DeckTabKind.FolderPortal ? tab.PortalPath
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(destination)) return;
        // Portal paste enforces the same path checks as Portal drag/drop and Ctrl+V.
        Func<string, string?>? portalGuard = tab.Kind == DeckTabKind.FolderPortal
            ? source => { ValidatePortalImport(source, destination); return null; }
            : null;
        DeckShellClipboardService.PasteFiles(destination, Dispatcher, result =>
        {
            if (!result.Completed)
            {
                if (!string.IsNullOrWhiteSpace(result.Error))
                    MessageBox.Show(this, result.Error, "CyanDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (result.WasCut) DeckCutClipboardVisualState.Clear();
            if (tab.Kind == DeckTabKind.Collection)
                AddDroppedPaths(tab, result.CreatedPaths);
            else if (SelectedTab?.Id == tab.Id)
                RefreshItems(IconMovementAnimationContext.ItemSetChanged);
        }, portalGuard);
    }

    // Shows Ctrl+1..9 next to the first nine tabs in the "탭" submenu.
    private void AnnotateTabShortcutGestures(MenuItem tabsMenu)
    {
        var count = Math.Min(Math.Min(Model.Tabs.Count, tabsMenu.Items.Count), 9);
        for (var index = 0; index < count; index++)
        {
            if (tabsMenu.Items[index] is MenuItem item)
                item.InputGestureText = "Ctrl+" + (index + 1).ToString(CultureInfo.InvariantCulture);
        }
        if (Model.Tabs.Count > 1)
            tabsMenu.ToolTip = "Ctrl+Tab: 다음 탭 · Ctrl+Shift+Tab: 이전 탭";
    }
}
