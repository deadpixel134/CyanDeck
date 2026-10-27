using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CyanDeck.Appearance;
using CyanDeck.Models;
using CyanDeck.Services;

namespace CyanDeck.Views;

/// <summary>
/// Item moves that keep the same item (tabs, groups, multi-selection), the item menu entries
/// for placement, the tab-removal confirmation and the real desktop file rename.
/// </summary>
public partial class FenceWindow
{
    // Carried next to FileDrop so a drop on another CyanDeck tab moves the same DeckItem
    // instead of creating a new reference. Explorer ignores this format.
    private const string ItemMoveDragFormat = "CyanDeck.Items";

    /// <summary>(source tab, item ids, target tab, copy instead of move)</summary>
    public event Action<Guid, IReadOnlyList<Guid>, Guid, bool>? ItemsMoveRequested;
    /// <summary>"자동 분류에 다시 맡기기" for the given tab and items.</summary>
    public event Action<Guid, IReadOnlyList<Guid>>? ItemsReturnToAutoRequested;
    /// <summary>Raised after confirmation, while the tab is still in the model.</summary>
    public event Action<FenceWindow, DeckTab>? TabRemoving;
    /// <summary>Raised after the tab was removed from the model.</summary>
    public event Action<FenceWindow, DeckTab>? TabRemoved;
    /// <summary>All groups, used for the "다른 그룹/탭으로 이동" menu.</summary>
    public Func<IReadOnlyList<DeckFence>>? MoveTargetFencesProvider { get; set; }

    private readonly Dictionary<Guid, (Border Element, DeckItem Item, double StartX, double StartY)> _freeGroupDragStarts = [];

    // ---- Drag payload -------------------------------------------------------------------

    private void AttachItemMovePayload(DataObject data, IEnumerable<ItemTile> tiles)
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection) return;
        var ids = tiles.Select(t => t.Source.Id)
            .Where(id => tab.Items.Any(i => i.Id == id))
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return;
        var idList = string.Join(",", ids.Select(id => id.ToString("D")));
        data.SetData(ItemMoveDragFormat, $"{tab.Id:D}|{idList}");
    }

    private IReadOnlyList<ItemTile> FreeDragPayloadTiles(Border source)
    {
        var selected = GetSelectedTiles();
        if (selected.Count > 0) return selected;
        if (source.Tag is ItemTile tile) return new[] { tile };
        return Array.Empty<ItemTile>();
    }

    /// <summary>
    /// A drop of CyanDeck items on a collection tab moves the same items there (Ctrl copies).
    /// Returns false for anything else so FileDrop/portal handling continues as before.
    /// </summary>
    private bool TryHandleItemMoveDrop(DragEventArgs e, DeckTab? target, bool selectTarget)
    {
        if (target is null || target.Kind != DeckTabKind.Collection || ItemsMoveRequested is null) return false;
        if (!e.Data.GetDataPresent(ItemMoveDragFormat)) return false;
        if (e.Data.GetData(ItemMoveDragFormat) is not string value) return false;
        var parts = value.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var sourceTabId)) return false;
        var ids = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => Guid.TryParse(text, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
        if (ids.Length == 0) return false;

        e.Handled = true;
        if (sourceTabId == target.Id) return true; // dropped back on its own tab
        var copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Link;
        if (selectTarget) SelectTab(target);
        var targetTabId = target.Id;
        // Apply after the drag loop has returned to the source window.
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => ItemsMoveRequested?.Invoke(sourceTabId, ids, targetTabId, copy)));
        return true;
    }

    /// <summary>
    /// Shows the copy cursor while Ctrl is held over CyanDeck items, so the cursor matches what
    /// TryHandleItemMoveDrop does. A plain move keeps the link cursor (Move is not offered so
    /// Explorer never moves the real file).
    /// </summary>
    private static bool IsItemCopyDrag(DragEventArgs e)
        => e.Data.GetDataPresent(ItemMoveDragFormat) && (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

    // ---- Free layout: drag every selected icon together ------------------------------

    private void BeginFreeGroupDrag(ItemTile pressed)
    {
        _freeGroupDragStarts.Clear();
        if (!_freeSelectedIds.Contains(pressed.Source.Id) || _freeSelectedIds.Count < 2) return;
        foreach (var id in _freeSelectedIds)
        {
            if (id == pressed.Source.Id) continue;
            if (!_freeBorders.TryGetValue(id, out var border) || border.Tag is not ItemTile tile) continue;
            var x = Canvas.GetLeft(border);
            var y = Canvas.GetTop(border);
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            _freeGroupDragStarts[id] = (border, tile.Source, x, y);
        }
    }

    /// <summary>Keeps one shared offset so the whole selection moves by the same distance.</summary>
    private (double Dx, double Dy) ClampFreeGroupDragOffset(double dx, double dy)
    {
        if (_freeGroupDragStarts.Count == 0) return (dx, dy);
        var minX = Math.Min(_freeDragStartX, _freeGroupDragStarts.Values.Min(v => v.StartX));
        var minY = Math.Min(_freeDragStartY, _freeGroupDragStarts.Values.Min(v => v.StartY));
        return (Math.Max(dx, -minX), Math.Max(dy, -minY));
    }

    private void MoveFreeGroupDrag(double dx, double dy)
    {
        foreach (var entry in _freeGroupDragStarts.Values)
        {
            var x = Math.Max(0, entry.StartX + dx);
            var y = Math.Max(0, entry.StartY + dy);
            Canvas.SetLeft(entry.Element, x);
            Canvas.SetTop(entry.Element, y);
            FreeCanvas.Width = Math.Max(FreeCanvas.Width, x + entry.Element.ActualWidth + 18);
            FreeCanvas.Height = Math.Max(FreeCanvas.Height, y + entry.Element.ActualHeight + 18);
        }
    }

    private void CommitFreeGroupDrag()
    {
        foreach (var entry in _freeGroupDragStarts.Values)
        {
            entry.Item.PositionX = Canvas.GetLeft(entry.Element);
            entry.Item.PositionY = Canvas.GetTop(entry.Element);
        }
        _freeGroupDragStarts.Clear();
    }

    private void RestoreFreeGroupDragPositions()
    {
        foreach (var entry in _freeGroupDragStarts.Values)
        {
            Canvas.SetLeft(entry.Element, entry.StartX);
            Canvas.SetTop(entry.Element, entry.StartY);
        }
        _freeGroupDragStarts.Clear();
    }

    // ---- Item menu entries ------------------------------------------------------------

    /// <summary>"다른 그룹/탭으로 이동" and "자동 분류에 다시 맡기기" for collection items.</summary>
    private void AddItemPlacementMenuEntries(ContextMenu menu, IReadOnlyList<ItemTile> tiles)
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection || tiles.Count == 0) return;
        var ids = tiles.Select(t => t.Source.Id).Distinct().ToArray();
        if (BuildMoveToTabMenu(tab, ids) is { } moveMenu) menu.Items.Add(moveMenu);
        if (tiles.Any(t => t.Source.UserPlaced))
        {
            var sourceTabId = tab.Id;
            menu.Items.Add(MakeItem("자동 분류에 다시 맡기기", () => ItemsReturnToAutoRequested?.Invoke(sourceTabId, ids)));
        }
    }

    /// <summary>The same commands for a Windows Shell menu, which only takes flat entries.</summary>
    private void AddItemPlacementShellExtras(List<ShellContextMenuService.ExtraCommand> extras, ItemTile tile)
    {
        var tab = SelectedTab;
        if (tab?.Kind != DeckTabKind.Collection) return;
        if (tile.Source.UserPlaced)
        {
            var sourceTabId = tab.Id;
            var id = tile.Source.Id;
            extras.Add(new("자동 분류에 다시 맡기기", () => ItemsReturnToAutoRequested?.Invoke(sourceTabId, new[] { id })));
        }
        if (DesktopRecycleService.IsActualDesktopItem(tile.Path))
            extras.Add(new("파일 이름 바꾸기…", () => RenameActualDesktopFile(tile)));
    }

    private MenuItem? BuildMoveToTabMenu(DeckTab current, IReadOnlyList<Guid> ids)
    {
        var fences = MoveTargetFencesProvider?.Invoke();
        if (fences is null) return null;
        var sourceTabId = current.Id;
        var root = new MenuItem { Header = "다른 그룹/탭으로 이동" };
        foreach (var fence in fences)
        {
            var targets = fence.Tabs.Where(t => t.Kind == DeckTabKind.Collection && t.Id != sourceTabId).ToArray();
            if (targets.Length == 0) continue;
            var fenceTitle = string.IsNullOrWhiteSpace(fence.Title) ? "이름 없는 그룹" : fence.Title;
            if (fence.Id == Model.Id) fenceTitle += " (이 그룹)";
            if (fence.Tabs.Count(t => t.Kind == DeckTabKind.Collection) == 1)
            {
                var only = targets[0].Id;
                root.Items.Add(MakeItem(fenceTitle, () => RequestItemsMove(sourceTabId, ids, only)));
                continue;
            }
            var fenceMenu = new MenuItem { Header = fenceTitle };
            foreach (var target in targets)
            {
                var targetId = target.Id;
                var tabTitle = string.IsNullOrWhiteSpace(target.Title) ? "이름 없는 탭" : target.Title;
                fenceMenu.Items.Add(MakeItem(tabTitle, () => RequestItemsMove(sourceTabId, ids, targetId)));
            }
            root.Items.Add(fenceMenu);
        }
        return root.Items.Count == 0 ? null : root;
    }

    private void RequestItemsMove(Guid sourceTabId, IReadOnlyList<Guid> ids, Guid targetTabId)
    {
        ClearFreeSelection();
        ItemsMoveRequested?.Invoke(sourceTabId, ids, targetTabId, false);
    }

    private void AddFileRenameMenuEntry(ContextMenu menu, ItemTile tile)
    {
        if (SelectedTab?.Kind != DeckTabKind.Collection || !DesktopRecycleService.IsActualDesktopItem(tile.Path)) return;
        menu.Items.Add(MakeItem("파일 이름 바꾸기…", () => RenameActualDesktopFile(tile)));
    }

    /// <summary>
    /// Renames the real desktop file or folder (explicit user command, separate from the
    /// display-name-only rename). The grouped reference is updated in place, so the item keeps
    /// its tab, position, display name and open count.
    /// </summary>
    private void RenameActualDesktopFile(ItemTile tile)
    {
        if (SelectedTab?.Kind != DeckTabKind.Collection) return;
        var source = tile.Path;
        if (!DesktopRecycleService.IsActualDesktopItem(source) || IsProtectedDeletionTarget(source))
        {
            System.Windows.MessageBox.Show(this,
                "바탕화면에 직접 저장된 파일·폴더만 여기서 이름을 바꿀 수 있습니다.",
                "CyanDeck — 원본 보호", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var oldName = Path.GetFileName(trimmed);
        // Explorer always hides the extension of shortcuts; edit only the visible part and keep it.
        var hiddenExtension = HiddenShortcutExtension(trimmed);
        var editableName = hiddenExtension.Length > 0 ? Path.GetFileNameWithoutExtension(oldName) : oldName;
        var typedName = TextPrompt.Show(this, "파일 이름 바꾸기", "바탕화면 파일의 새 이름", editableName);
        if (string.IsNullOrWhiteSpace(typedName)) return;
        var newName = typedName.Trim();
        if (hiddenExtension.Length > 0 && !newName.EndsWith(hiddenExtension, StringComparison.OrdinalIgnoreCase))
            newName += hiddenExtension;
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;
        if (WindowsFileNamePolicy.GetProblem(newName) is { } nameProblem)
        {
            System.Windows.MessageBox.Show(this, nameProblem, "CyanDeck — 파일 이름 바꾸기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(parent)) return;
        var destination = Path.Combine(parent, newName);
        var caseOnly = string.Equals(trimmed, destination, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
        {
            System.Windows.MessageBox.Show(this, "같은 이름의 항목이 이미 있습니다.", "CyanDeck — 파일 이름 바꾸기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (File.Exists(trimmed)) File.Move(trimmed, destination);
            else Directory.Move(trimmed, destination);
            tile.Source.Path = DesktopDirectoryService.Normalize(destination);
            tile.Source.UserPlaced = true;
            RefreshItems(IconMovementAnimationContext.ItemSetChanged);
            ModelChanged?.Invoke();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Warn($"Desktop file rename failed: {ex.GetType().Name}");
            var message = ex switch
            {
                UnauthorizedAccessException => "이 항목의 이름을 바꿀 권한이 없습니다. 공용 바탕화면 항목은 관리자 권한이 필요할 수 있습니다.",
                FileNotFoundException or DirectoryNotFoundException => "원래 파일을 찾을 수 없습니다. 이미 이동했거나 삭제되었을 수 있습니다.",
                IOException => "파일이 사용 중이거나 지금은 이름을 바꿀 수 없습니다. 열린 프로그램을 닫고 다시 시도하세요.",
                _ => "이름을 바꾸지 못했습니다."
            };
            System.Windows.MessageBox.Show(this, message, "CyanDeck — 파일 이름 바꾸기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>".lnk"/".url" (always hidden by Explorer) or an empty string.</summary>
    private static string HiddenShortcutExtension(string path)
    {
        if (Directory.Exists(path)) return "";
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase)
            ? extension
            : "";
    }

    // ---- Tab removal ------------------------------------------------------------------

    /// <summary>Tab removal asks first, like group deletion, and says where desktop files go.</summary>
    private bool ConfirmRemoveTab(DeckTab tab)
    {
        var name = string.IsNullOrWhiteSpace(tab.Title) ? "이 탭" : $"'{tab.Title}' 탭";
        string details;
        if (tab.Kind == DeckTabKind.FolderPortal)
        {
            details = "폴더 포털 연결과 보기 설정이 제거됩니다. 실제 폴더와 파일은 그대로 유지됩니다.";
        }
        else
        {
            var roots = DesktopDirectoryService.GetRoots();
            var desktopCount = tab.Items.Count(i =>
                !DesktopSystemItems.IsSupportedVirtualPath(i.Path) && DesktopDirectoryService.IsDirectChildOfAnyRoot(i.Path, roots));
            var lines = new List<string> { $"아이콘 {tab.Items.Count}개와 자동 분류 규칙 {tab.Rules.Count}개가 함께 제거됩니다." };
            if (desktopCount > 0)
                lines.Add($"바탕화면 파일 {desktopCount}개는 다른 탭으로 다시 분류되고, 들어갈 탭이 없으면 그룹 밖 바탕화면 아이콘으로 남습니다.");
            lines.Add("실제 파일과 폴더는 삭제되지 않습니다.");
            details = string.Join("\n", lines);
        }
        var answer = System.Windows.MessageBox.Show(this,
            $"{name}을(를) 제거하시겠습니까?\n\n{details}",
            "탭 제거", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }
}
