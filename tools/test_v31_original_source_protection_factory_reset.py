#!/usr/bin/env python3
"""Static source-level safety guards for v31. NOT a substitute for Windows GUI tests."""
from pathlib import Path
from xml.etree import ElementTree as ET
import sys
base=Path(__file__).resolve().parents[1]
def read(name): return (base/name).read_text(encoding='utf-8')
f=read('src/CyanDeck/Views/FenceWindow.xaml.cs')
k=read('src/CyanDeck/Views/FenceWindow.Keyboard.cs')
a=read('src/CyanDeck/Services/AppController.cs')
x=read('src/CyanDeck/Views/SettingsWindow.xaml')
c=read('src/CyanDeck/Views/SettingsWindow.xaml.cs')
store=read('src/CyanDeck/Services/StateStore.cs')
model=read('src/CyanDeck/Models/Models.cs')
shell=read('src/CyanDeck/Services/ShellContextMenuService.cs')
zone=f[f.index('private void DeleteItems('): f.index('private void OpenItem(')]
remove=f[f.index('private void RemoveCollectionReferences('): f.index('private void DeleteItems(')]
rename=f[f.index('private void RenameItem('):f.index('private void DeleteItem(')]
reset=a[a.index('private bool ResetCyanDeckToDefaults()'):a.index('private void ReplaceState(')]
# v70: groups show the Windows Shell menu only for REAL Desktop children
# (same rule as loose desktop icons); external Collection references stay CyanDeck-only.
items_shell=read('src/CyanDeck/Views/FenceWindow.ItemsShell.cs')
shell_gate=items_shell[items_shell.index('private bool CanShowNativeShellMenu('):items_shell.index('private bool TryHandleNativeShellVerb(')]
collection_shell_gated=('if (tab.Kind != DeckTabKind.Collection) return true;' in shell_gate and
    'list.All(DesktopRecycleService.IsActualDesktopItem)' in shell_gate)
checks={
'collection Delete routed to ref removal before File IO':zone.index('RemoveCollectionReferences(tiles)')<zone.index('FileSystem.DeleteFile'),
'collection deletion remains guarded from Portal and other paths': 'if (SelectedTab?.Kind == DeckTabKind.Collection)' in zone and 'if (SelectedTab?.Kind != DeckTabKind.FolderPortal) return;' in zone and 'return;' in zone,
'non portal deletions fail closed': 'if (SelectedTab?.Kind != DeckTabKind.FolderPortal) return;' in zone,
'collection uses source IDs not path-based delete': 'var ids = tiles.Select(t => t.Source.Id).ToHashSet();' in remove,
'removal solely changes Tab.Items': 'tab.Items.RemoveAll(i => ids.Contains(i.Id));' in remove,
'confirmation says originals preserved': '실제 원본 파일·폴더는 삭제하거나 이동하지 않습니다.' in remove,
'collection external refs no shell for single context menu': collection_shell_gated and 'CanShowNativeShellMenu([tile.Path]) &&\n            !IsProtectedDeletionTarget(tile.Path) &&\n            ShellContextMenuService.TryShow(this, tile.Path' in f,
'collection external refs no shell for multi context menu': collection_shell_gated and 'CanShowNativeShellMenu(existing.Select(t => t.Path)) &&\n            existing.All(t => !IsProtectedDeletionTarget(t.Path)) &&\n            ShellContextMenuService.TryShow(this, existing.Select' in f,
'single UI explicitly group-only': '그룹에서만 제거 (원본 유지)' in f,
'batch UI explicitly group-only': '선택 항목을 그룹에서만 제거 (원본 유지)' in f,
'Delete and Shift Delete both go through guarded method': 'e.Key == Key.Delete' in k and ('DeleteItems(selected.Count > 0 ? selected' in k or 'DeleteItems(deleting);' in k),
'root protected': 'IsProtectedDeletionTarget(t.Path)' in zone and 'Path.GetPathRoot(full)' in zone,
'critical user profile protected': 'SpecialFolder.UserProfile' in zone,
'critical windows directory protected': 'SpecialFolder.Windows' in zone,
'portal native context menu protects roots': 'existing.All(t => !IsProtectedDeletionTarget(t.Path))' in f and '!IsProtectedDeletionTarget(tile.Path) &&' in f,
'portal rename protects critical paths': 'if (IsProtectedDeletionTarget(tile.Path))' in rename,
'portal drop protects original critical paths': 'if (IsProtectedDeletionTarget(sourceFull))' in f,
'known downloads root protected': 'SpecialFolder.UserProfile), "Downloads"' in f,
'portal existing recycle command still used': 'RecycleOption.SendToRecycleBin' in zone,
'collection rename touches UI label only': 'tile.Source.DisplayNameOverride = newLabel;' in rename,
'collection rename returns before filesystem rename': rename.index('return;\n        }') < rename.index('File.Move('),
'drop into collection stores DeckItem reference': 'tab.Items.Add(new DeckItem { Path = path });' in f,
'source filesystem context menu invoked only for Portal or real Desktop children': collection_shell_gated and f.count('CanShowNativeShellMenu(') >= 2,
'factory reset button present': 'x:Name="FactoryResetButton"' in x,
'factory reset button handler present': 'Click="FactoryReset_Click"' in x and 'private void FactoryReset_Click(' in c,
'factory reset navigation present': 'Tag="AboutTab|FactoryResetSection"' in x,
'normal tray exposes replayable first-run guide': 'menu.Items.Add("시작 가이드 다시 보기…", null' in a[a.index('menu.Items.Add(startup);'):a.index('private void UpdateTrayText()')],
'initial group reset uses disk reimport rather than retaining deleted items': 'CreateDefaultLayout();' in a[a.index('private bool ResetVisualGeometryToDefaults('):a.index('private bool ResetCyanDeckToDefaults()')] and 'RuleEngine.EnumerateDesktopItems()' in a[a.index('private bool ResetVisualGeometryToDefaults('):a.index('private bool ResetCyanDeckToDefaults()')],
'all-window reset exposed independently from destructive group restore': 'ResetAllWindowGeometryButton' in x and 'ResetAllWindowGeometry_Click' in c and 'ResetAllWindowGeometryToDefaults' in a and 'resetGroupContent: false' in a,
'tray menu categorizes commands without discarding callbacks': 'CompactTrayMenu(menu);' in a and 'var calendar = new Forms.ToolStripMenuItem("캘린더")' in a and 'var recovery = new Forms.ToolStripMenuItem("백업 및 복구")' in a,
'factory reset handler wired to controller': ('ResetCyanDeckToDefaults);' in a or
    ('ResetCyanDeckToDefaults,' in a and
     ('ResetVisualGeometryToDefaults);' in a or
      'ResetVisualGeometryToDefaults, ResetAllWindowGeometryToDefaults);' in a or
      '(resetGroups, resetCalendar) => ResetVisualGeometryToDefaults(resetGroups, resetCalendar)' in a))) and
    'private readonly Func<bool> _factoryReset;' in c and
    'private readonly Func<bool, bool, bool> _resetPlacement;' in c,
'reset requires user confirmation': 'MessageBoxButton.YesNo' in reset and "'초기화'" in reset,
'reset refuses safe mode/read-only': 'if (_compatibilityReadOnly || _safeMode) return false;' in reset,
'reset enforces managed policy': 'EnsureManagedCapability("Settings.EditGeneral"' in reset,
'reset writes user-recoverable backup': '_store.Export(_state, backup);' in reset and 'reset-backups' in reset,
'reset saves recovery checkpoint': '_store.SaveRecoveryCheckpoint(_state, "before-factory-reset")' in reset,
'reset refuses to continue without backup': reset.index('_store.Export(_state, backup)') < reset.index('ReplaceState(defaults)'),
'reset avoids deleting calendar files and tokens': not any(phrase in reset for phrase in ['File.Delete(', 'Directory.Delete(', 'calendar-events.json")', 'token.json")']),
'reset preserves OAuth Client ID': 'defaults.Settings.GoogleCalendarClientId = _state.Settings.GoogleCalendarClientId;' in reset,
'reset new state persists durably before message': reset.index('PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true);') < reset.index('초기화했습니다.'),
'reset startup restart invoked': 'Restart(false);' in reset,
'reset rollback on failure': 'ReplaceState(_store.Import(backup))' in reset,
'schema remains 35': 'Version { get; set; } = 35' in model,
'no system-wide keyboard hook introduced': 'WH_KEYBOARD_LL' not in f and 'WH_KEYBOARD_LL' not in reset,
'no shell global delete interception introduced': 'InvokeCommand(ref invoke)' in shell,
'XAML well formed': True,
}
try: ET.fromstring(x)
except Exception: checks['XAML well formed']=False
for name, ok in checks.items(): print(('PASS' if ok else 'FAIL')+': '+name)
print(f'RESULT {sum(checks.values())}/{len(checks)}')
sys.exit(0 if all(checks.values()) else 1)
