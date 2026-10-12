#!/usr/bin/env python3
"""v33 source-contract tests (no Windows WPF runtime available here)."""
from pathlib import Path
import sys
root=Path(__file__).resolve().parents[1]
view=(root/'src/DesktopDeck/Views/FenceWindow.xaml.cs').read_text()
keyboard=(root/'src/DesktopDeck/Views/FenceWindow.Keyboard.cs').read_text()
dialog=(root/'src/DesktopDeck/Views/CollectionDesktopDeleteDialog.cs').read_text()
recycler=(root/'src/DesktopDeck/Services/DesktopRecycleService.cs').read_text()
clipboard=(root/'src/DesktopDeck/Services/DeckShellClipboardService.cs').read_text()
choose=view[view.index('private void DeleteItems('): view.index('private static bool IsProtectedDeletionTarget(')]
explicit=view[view.index('private void RecycleActualDesktopCollectionItems('):view.index('private void RemoveCollectionReferences(')]
remove=view[view.index('private void RemoveCollectionReferences('):view.index('private void DeleteItems(')]
checks={
 'Delete only offers choice to Collection tabs':'if (SelectedTab?.Kind == DeckTabKind.Collection)' in choose,
 'empty selection cannot trigger destructive choice':'tiles.Count > 0' in choose,
 'all selected items must be direct Desktop children':'tiles.All(t => DesktopRecycleService.IsActualDesktopItem(t.Path))' in choose,
 'Delete displays explicit three-way decision':'CollectionDesktopDeleteDialog.Show(this, tiles.Count)' in choose,
 'recycle only on distinct recycle choice':'case CollectionDesktopDeleteChoice.Recycle:' in choose and 'RecycleActualDesktopCollectionItems(tiles, confirmedByChoiceDialog: true);' in choose,
 'reference deletion only on distinct group-only choice':'case CollectionDesktopDeleteChoice.RemoveFromGroup:' in choose and 'RemoveCollectionReferences(tiles, confirmedByChoiceDialog: true);' in choose,
 'mixed or outside selection only removes references':'else\n            {\n                RemoveCollectionReferences(tiles);\n            }' in choose,
 'collection delete returns before Portal destructive code':choose.index('return;') < choose.index('if (SelectedTab?.Kind != DeckTabKind.FolderPortal) return;'),
 'same choice used by context menu':'() => DeleteItem(tile)' in view and '() => DeleteItems(existing)' in view,
 'single menu announces choice':'삭제… (휴지통 또는 그룹에서만 제거)' in view,
 'batch menu announces choice':'선택 항목 삭제… (휴지통 또는 그룹에서만 제거)' in view,
 'external context action remains group-only':'그룹에서만 제거 (원본 유지)' in view,
 'recycle method checks collection context':'tab?.Kind != DeckTabKind.Collection' in explicit,
 'recycle method validates EVERY path again':'paths.Any(p => !DesktopRecycleService.IsActualDesktopItem(p))' in explicit,
 'recycler validates every path again':'if (!IsActualDesktopItem(path))' in recycler,
 'recycle method still asks YES/NO for Ctrl+Delete':'!confirmedByChoiceDialog && System.Windows.MessageBox.Show(this,' in explicit,
 'recycle confirm has safe NO default':'MessageBoxResult.No' in explicit,
 'only successfully recycled sources removed':'result.Recycled.ToHashSet' in explicit and 'moved.Contains(i.Path)' in explicit,
 'reference-only action confirms outside choice':'!confirmedByChoiceDialog && System.Windows.MessageBox.Show(this, message' in remove,
 'reference removal changes no physical files':'File.Delete(' not in remove and 'Directory.Delete(' not in remove and 'FileSystem.DeleteFile' not in remove,
 'reference removal by item ID':'tab.Items.RemoveAll(i => ids.Contains(i.Id));' in remove,
 'popup has correct action names':all(t in dialog for t in ('휴지통으로 이동','그룹에서만 제거','취소')),
 'popup is not always on top':'Topmost = false;' in dialog,
 'popup has no default delete action':'IsDefault = false' in dialog,
 'popup default state is Cancel':'_choice = CollectionDesktopDeleteChoice.Cancel;' in dialog,
 'popup close action is cancel':'dialog.ShowDialog() == true ? dialog._choice : CollectionDesktopDeleteChoice.Cancel' in dialog,
 'no clipboard code changed':'FoMove = 0x0001' in clipboard and 'FoCopy = 0x0002' in clipboard and 'SHFileOperation(ref operation)' in clipboard,
 'Ctrl+Delete remains explicit':'Keyboard.Modifiers == ModifierKeys.Control' in keyboard and 'RecycleActualDesktopCollectionItems(deleting);' in keyboard,
 'plain Delete routes to choice':'DeleteItems(deleting);' in keyboard,
 'Portal still recycles only':'RecycleOption.SendToRecycleBin' in choose,
 'Windows Shell context remains disabled for Collection':'SelectedTab?.Kind != DeckTabKind.Collection &&' in view,
 'no new global hook':all('WH_KEYBOARD_LL' not in t for t in (dialog,keyboard,choose)),
}
for name,ok in checks.items(): print(('PASS' if ok else 'FAIL')+': '+name)
print('RESULT',sum(checks.values()),'/',len(checks))
sys.exit(0 if all(checks.values()) else 1)
