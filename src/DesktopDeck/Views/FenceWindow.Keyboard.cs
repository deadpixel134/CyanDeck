using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using DesktopDeck.Interop;
using DesktopDeck.Animations;
using DesktopDeck.Appearance;
using DesktopDeck.Models;
using DesktopDeck.Layout;
using DesktopDeck.ShellIntegration;
using DesktopDeck.Services;
using Forms = System.Windows.Forms;

namespace DesktopDeck.Views;

/// <summary>Fence keyboard command routing, decoupled from rendering and menus.</summary>
public partial class FenceWindow
{
    private void HandleWindowKeyboardCommand(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.F)
        {
            ShowSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SearchPanel.Visibility == Visibility.Visible)
        {
            SearchClose_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DeckCutClipboardVisualState.HasCut &&
            SearchPanel.Visibility != Visibility.Visible)
        {
            DeckShellClipboardService.CancelPendingCut(Dispatcher);
            DeckCutClipboardVisualState.Clear();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.A &&
            SearchPanel.Visibility != Visibility.Visible && Keyboard.FocusedElement is not TextBox)
        {
            SelectAllItems();
            e.Handled = true;
            return;
        }

        // Explorer-style file clipboard commands are window-local. A focused text
        // editor retains its own native text copy/paste; no global keyboard monitor.
        if (Keyboard.FocusedElement is TextBox) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == ModifierKeys.Control)
        {
            if (e.Key == Key.C || e.Key == Key.X)
            {
                var selectedPaths = GetSelectedTiles().Select(tile => tile.Path)
                    .Where(path => System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (selectedPaths.Length == 0 && KeyboardSelectedTile() is { } focused &&
                    (System.IO.File.Exists(focused.Path) || System.IO.Directory.Exists(focused.Path)))
                    selectedPaths = [focused.Path];
                if (selectedPaths.Length > 0)
                {
                    var cut = e.Key == Key.X;
                    DeckShellClipboardService.CopyFiles(selectedPaths, cut, Dispatcher,
                        error =>
                        {
                            if (error is not null)
                                MessageBox.Show(this, error, "DesktopDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
                            else if (cut) DeckCutClipboardVisualState.Mark(selectedPaths);
                            else DeckCutClipboardVisualState.Clear();
                        });
                    e.Handled = true;
                }
                return;
            }
            if (e.Key == Key.V)
            {
                var tab = SelectedTab;
                if (tab is null) return;
                // Collection tabs are virtual rather than physical folders. Copy
                // onto the user's actual (possibly OneDrive-redirected) Desktop,
                // then include the created paths in this Collection tab.
                var destination = tab.Kind == DeckTabKind.FolderPortal ? tab.PortalPath
                    : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(destination)) return;
                // Portal Ctrl+V must enforce the same path checks as Portal drag/drop.
                // Collection Ctrl+V retains its original Explorer-style behavior.
                Func<string, string?>? portalGuard = tab.Kind == DeckTabKind.FolderPortal
                    ? source => { ValidatePortalImport(source, destination); return null; }
                    : null;
                DeckShellClipboardService.PasteFiles(destination, Dispatcher, result =>
                {
                    if (!result.Completed)
                    {
                        if (!string.IsNullOrWhiteSpace(result.Error))
                            MessageBox.Show(this, result.Error, "DesktopDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    if (result.WasCut) DeckCutClipboardVisualState.Clear();
                    if (tab.Kind == DeckTabKind.Collection)
                        AddDroppedPaths(tab, result.CreatedPaths);
                    else if (SelectedTab?.Id == tab.Id)
                        RefreshItems(IconMovementAnimationContext.ItemSetChanged);
                }, portalGuard);
                e.Handled = true;
                return;
            }
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Tab)
        {
            if (Model.Tabs.Count > 1)
            {
                var delta = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                var next = (Model.SelectedTabIndex + delta + Model.Tabs.Count) % Model.Tabs.Count;
                SelectTab(Model.Tabs[next]);
            }
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            var index = (int)e.Key - (int)Key.D1;
            if (index < Model.Tabs.Count) SelectTab(Model.Tabs[index]);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            if (e.SystemKey == Key.Left) { NavigatePortalBack(); e.Handled = true; return; }
            if (e.SystemKey == Key.Right) { NavigatePortalForward(); e.Handled = true; return; }
            if (e.SystemKey == Key.Up) { NavigatePortalUp(); e.Handled = true; return; }
        }

        var selected = GetSelectedTiles();
        var tile = KeyboardSelectedTile();
        if (tile is null && selected.Count == 0) return;
        if (e.Key == Key.Enter)
        {
            if (selected.Count > 1)
            {
                foreach (var item in selected.Take(12)) Open(item.Path);
            }
            else if (tile is not null && SelectedTab?.Kind == DeckTabKind.FolderPortal && Directory.Exists(tile.Path))
            {
                OpenPortalFolder(tile.Path);
            }
            else if (tile is not null) Open(tile.Path);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && selected.Count <= 1 && tile is not null)
        {
            RenameItem(tile);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            var deleting = selected.Count > 0 ? selected : tile is null ? [] : [tile];
            // Ctrl+Delete explicitly requests recycling of actual Desktop children.
            // Plain Delete/Shift+Delete shows a three-choice confirmation for
            // Desktop-only Collection selections; external paths remain references.
            if (SelectedTab?.Kind == DeckTabKind.Collection &&
                Keyboard.Modifiers == ModifierKeys.Control &&
                deleting.Count > 0 && deleting.All(t => DesktopRecycleService.IsActualDesktopItem(t.Path)))
                RecycleActualDesktopCollectionItems(deleting);
            else
                DeleteItems(deleting);
            e.Handled = true;
        }
    }
}
