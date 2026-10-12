#!/usr/bin/env python3
"""v32 source safety contract checks. Does NOT verify Win32 Recycle Bin behavior."""
from pathlib import Path
import sys
r=Path(__file__).resolve().parents[1]
f=(r/'src/DesktopDeck/Views/FenceWindow.xaml.cs').read_text(encoding='utf-8')
k=(r/'src/DesktopDeck/Views/FenceWindow.Keyboard.cs').read_text(encoding='utf-8')
l=(r/'src/DesktopDeck/Services/LooseDesktopIconHost.cs').read_text(encoding='utf-8')
s=(r/'src/DesktopDeck/Services/DesktopRecycleService.cs').read_text(encoding='utf-8')
resolver=(r/'src/DesktopDeck/Services/DesktopDirectoryService.cs').read_text(encoding='utf-8')
collection=f[f.index('private void RemoveCollectionReferences('):f.index('private void DeleteItems(')]
normal=f[f.index('private void DeleteItems('): f.index('private static bool IsProtectedDeletionTarget(')]
explicit=f[f.index('private void RecycleActualDesktopCollectionItems('):f.index('private void RemoveCollectionReferences(')]
loosemenu=l[l.index('private void ShowContextMenu('):l.index('private static void OpenFallbackContextMenu(')]
checks={
    'known desktop roots resolved dynamically': 'DesktopDirectoryService.GetRoots()' in s,
    'immediate children only': 'IsDirectChildOfAnyRoot' in s and 'IsDirectChild(' in resolver,
    'no hardcoded Desktop directory path': 'CHOSUN' not in s and 'C:\\Users' not in s,
    'filesystem guard rejects Shell namespace': 'IsShellNamespacePath(path)' in s,
    'public Desktop root protected in Portal': 'SpecialFolder.CommonDesktopDirectory' in f and 'Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory))' in f,
    'redirected Desktop roots protected in Portal': 'criticalPaths.Concat(DesktopDirectoryService.GetRoots())' in f,
    'filesystem guard rejects junctions/reparse points': 'FileAttributes.ReparsePoint' in s,
    'physical deletion fails closed': 'if (!IsActualDesktopItem(path))' in s,
    'file recycle explicitly not permanent': 'FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin' in s,
    'folder recycle explicitly not permanent': 'FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin' in s,
    'no permanent-delete fallback': 'DeletePermanently' not in s and 'Directory.Delete(' not in s and 'File.Delete(' not in s,
    'recycle verifies source moved': 'if (File.Exists(path) || Directory.Exists(path))' in s,
    'collection Delete offers recycle only for actual Desktop paths': 'tiles.All(t => DesktopRecycleService.IsActualDesktopItem(t.Path))' in normal and 'CollectionDesktopDeleteDialog.Show(this, tiles.Count)' in normal,
    'collection reference removal touches layout': 'tab.Items.RemoveAll(i => ids.Contains(i.Id));' in collection,
    'actual-delete method limited to collection tabs': 'tab?.Kind != DeckTabKind.Collection' in explicit,
    'actual-delete method validates full batch': 'paths.Any(p => !DesktopRecycleService.IsActualDesktopItem(p))' in explicit,
    'actual-delete confirmation before file operation': explicit.index('MessageBox.Show(this,') < explicit.index('DesktopRecycleService.RecycleConfirmed(paths,'),
    'actual-delete confirmation default NO': 'MessageBoxResult.No' in explicit,
    'actually recycled entries only removed from group': 'result.Recycled.ToHashSet(' in explicit and 'moved.Contains(i.Path)' in explicit,
    'single desktop collection menu': 'DesktopRecycleService.IsActualDesktopItem(tile.Path)' in f and '바탕화면 원본을 휴지통으로 이동' in f,
    'batch mixed external selection has no delete action': 'existing.All(t => DesktopRecycleService.IsActualDesktopItem(t.Path))' in f,
    'plain Delete and Shift Delete use guarded choice path': 'Keyboard.Modifiers == ModifierKeys.Control' in k and 'DeleteItems(deleting);' in k,
    'Ctrl Delete only for all real desktop paths': 'deleting.All(t => DesktopRecycleService.IsActualDesktopItem(t.Path))' in k,
    'does not reinstate shell context for collection': 'SelectedTab?.Kind != DeckTabKind.Collection &&' in f,
    'loose external paths cannot access native delete menu': 'paths.All(DesktopRecycleService.IsActualDesktopItem)' in loosemenu,
    'loose menu allows physical desktop deletion': 'selectedDesktopPaths.All(DesktopRecycleService.IsActualDesktopItem)' in l,
    'loose menu uses shared safe recycler': 'DesktopRecycleService.RecycleConfirmed(paths,' in l,
    'no keyboard low-level hook': 'WH_KEYBOARD_LL' not in s+k+l,
}
for name, ok in checks.items(): print(('PASS' if ok else 'FAIL')+': '+name)
print(f'RESULT {sum(checks.values())}/{len(checks)}')
sys.exit(0 if all(checks.values()) else 1)
