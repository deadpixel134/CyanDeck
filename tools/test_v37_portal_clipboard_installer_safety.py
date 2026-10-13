#!/usr/bin/env python3
"""v37 non-destructive source checks. Does not execute any PC file operation."""
from pathlib import Path
r=Path(__file__).resolve().parents[1]
def read(path): return (r/path).read_text(encoding='utf-8-sig')
fence=read('src/CyanDeck/Views/FenceWindow.xaml.cs')
keyboard=read('src/CyanDeck/Views/FenceWindow.Keyboard.cs')
clip=read('src/CyanDeck/Services/DeckShellClipboardService.cs')
inst=read('install.ps1')
chk={
    'portal unified validator exists': 'private static string ValidatePortalImport(string source, string portal)' in fence,
    'drag uses shared validator': 'var destination = ValidatePortalImport(sourceFull, tab.PortalPath);' in fence,
    'paste uses shared validator': 'source => { ValidatePortalImport(source, destination); return null; }' in keyboard,
    'paste validator only portal': 'tab.Kind == DeckTabKind.FolderPortal\n                    ? source =>' in keyboard,
    'collection clipboard semantics retained': ': Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)' in keyboard,
    'paste worker accepts validator': 'Func<string, string?>? validateSource = null' in clip,
    'clipboard guarded before any shell operation': clip.index('if (validateSource is not null)') < clip.index('var code = SHFileOperation(ref operation);'),
    'clipboard validates all copied sources': 'foreach (var source in files)' in clip,
    'clipboard preflight catches unsafe paths': '보호된 파일 작업을 차단했습니다' in clip,
    'portal drive/user roots protected': 'if (IsProtectedDeletionTarget(sourceFull))' in fence,
    'portal target roots protected': 'if (IsProtectedDeletionTarget(portalFull))' in fence,
    'portal destination protected': 'if (IsProtectedDeletionTarget(destination))' in fence,
    'portal blocks self or descendant': 'destination.StartsWith(sourceFull.TrimEnd(Path.DirectorySeparatorChar)' in fence,
    'portal blocks source already inside portal': 'sourceFull.StartsWith(portalFull.TrimEnd(Path.DirectorySeparatorChar)' in fence,
    'portal preflight rejects missing source': 'if (!File.Exists(sourceFull) && !Directory.Exists(sourceFull))' in fence,
    'portal rejects reparse ancestors': 'File.GetAttributes(current)' in fence and 'FileAttributes.ReparsePoint' in fence,
    'staging copy uses new unique directory': '".CyanDeck-partial-copy-" + Guid.NewGuid()' in fence,
    'staging only confirmed by rename': 'CopyDirectoryTree(source, staging);' in fence and 'Directory.Move(staging, destination);' in fence,
    'copy never recursively deletes destination': 'Directory.Delete(destination' not in fence and 'Directory.Delete(staging, recursive: true);' in fence,
    'staging cleanup checks reparse': 'EnsureNoReparseInTree(staging);' in fence,
    'staging cleanup only on error': fence.index('catch\n        {\n            // The staging path') < fence.index('Directory.Delete(staging, recursive: true)'),
    'installer validates path chain': 'function Assert-NoReparseAncestors' in inst and 'Get-Item -LiteralPath $cursor' in inst,
    'installer rejects links': 'CyanDeck installer refused reparse-point path' in inst,
    'installer checks destination before copying': inst.index('Assert-NoReparseAncestors $installDir') < inst.index('Copy-Item $source'),
    'installer checks owned file targets': 'Assert-NoReparseAncestors (Join-Path $installDir $name)' in inst,
    'installer preflights required helper files before writing': all(fragment in inst for fragment in ("'uninstall.ps1','apply-update.ps1','recover-desktop-icons.ps1'", 'Assert-NoReparseAncestors $helperSource', 'Required CyanDeck installer helper is missing:')) and inst.index('Required CyanDeck installer helper is missing:') < inst.index('Copy-Item $source'),
    'installer protects recovery helper destination': "'apply-update.ps1','recover-desktop-icons.ps1'" in inst and 'Assert-NoReparseAncestors (Join-Path $installDir $name)' in inst,
    'installer checks source EXE': 'Assert-NoReparseAncestors $source' in inst,
    'installer checks existing shortcuts': 'Assert-ShortcutBelongsToCyanDeck' in inst,
    'installer refuses foreign shortcut': 'CyanDeck installer refused to overwrite an unrelated shortcut' in inst,
    'installer never kills running process': 'Stop-Process -Force' not in inst,
    'installer no recursive wipe': 'Remove-Item' not in inst,
    'settings schema remains 35': 'Current = 35' in read('src/CyanDeck/Services/StateSchema.cs'),
    'Google token not packaged': not (r/'json/client_secret.json').exists(),
    'existing v36 updater remains present': 'function Install-ManagedFiles' in read('apply-update.ps1'),
}
for name, ok in chk.items(): print(('PASS' if ok else 'FAIL') + ': ' + name)
print(f'v37 safety regression: {sum(chk.values())}/{len(chk)}')
if not all(chk.values()): raise SystemExit(1)
