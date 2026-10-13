#!/usr/bin/env python3
"""Non-destructive v42 source regression tests: no updates, shell calls, or hooks executed."""
from pathlib import Path
r=Path(__file__).resolve().parents[1]
def source(x): return (r/x).read_text(encoding='utf-8-sig')
up=source('apply-update.ps1')
ctl=source('src/CyanDeck/Services/AppController.cs')
svc=source('src/CyanDeck/Services/UpdateService.cs')
rescue=source('recover-desktop-icons.ps1')
checks={
 'backup snapshot fully verified before replacement': up.count('Assert-SnapshotMatches $InstallDir $safety') == 2,
 'rollback storage rejects reparse point chain': "Assert-OrdinaryDirectory $rollbackRoot 'Rollback storage'" in up,
 'journal keyed by canonical install location': 'Get-RecoveryJournal' in up and 'ToUpperInvariant()' in up and 'SHA256' in up and 'pending-$key.json' in up,
 'journal written before file changes in both paths': up.count('Write-RecoveryJournal $journal $safety')==2 and all(up.find('Write-RecoveryJournal $journal $safety',p) < up.find('Install-ManagedFiles',p) for p in [up.find('    Copy-ManagedSnapshot $InstallDir $safety'),up.rfind('    Copy-ManagedSnapshot $InstallDir $safety')]),
 'one recovery record not silently overwritten': 'if (Test-Path -LiteralPath $Journal) { throw' in up,
 'journal created by atomic move': '[IO.File]::Move($temporary, $Journal)' in up,
 'new updates blocked when recovery unresolved': "if (Test-Path -LiteralPath $journal) {\n  throw" in up,
 'manual recovery switch': '[switch]$RecoverPending' in up and 'Restore-PendingUpdate $journal' in up,
 'restore can run with missing executable': 'if (-not $RecoverPending -and -not (Test-Path -LiteralPath $installedExe' in up,
 'restoration source confined under owned rollback directory': "Recovery snapshot is outside CyanDeck-owned backup storage." in up,
 'journal remains if automatic verification fails': 'Recovery verification failed for' in up and up.index('Recovery verification failed for') < up.index('Remove-Item -LiteralPath $Journal -Force'),
 'restoration compares full file hashes': '(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash' in up and '(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash' in up,
 'no recursive deletion of install root': 'Remove-Item -LiteralPath $InstallDir -Recurse' not in up and 'Stop-Process -Force' not in up,
 'source errors never fall back to deleting entire directory': 'Get-ChildItem -LiteralPath $InstallDir -Force | Remove-Item' not in up,
 'desktop startup exposes pending recovery': 'HasPendingRecovery()' in ctl and 'OfferPendingUpdateRecovery' in ctl and 'TryLaunchPendingRecovery(Environment.ProcessId)' in ctl,
 'startup restores only after explicit consent': 'MessageBoxButton.YesNo' in ctl[ctl.index('private void OfferPendingUpdateRecovery'):ctl.index('private void LaunchPendingUpdateRecovery')],
 'recovery process waits normal termination': '-WaitForPid {waitForPid}' in svc and 'Wait-CyanDeckProcess $WaitForPid' in up,
 'emergency icon rescue available even in safe mode': ctl.index('Windows Explorer 기본 아이콘 긴급 복구') < ctl.index('if (!_safeMode && !_state.Settings.EnableCyanDeck)'),
 'emergency command verifies live Explorer FolderView': 'TryRestoreNativeDesktopIcons()' in ctl and 'public bool TryRestoreNativeDesktopIcons()' in source('src/CyanDeck/Services/DesktopHost.cs'),
 'native icons forced visible in safe mode': 'if (runtimePlan.IsSafeMode)' in ctl and 'try { _host.SetNativeDesktopIconsVisible(true); }' in ctl,
 'icon rescue override persists across shell retry': '_forceNativeIconsVisibleUntilRestart' in ctl and 'if (_forceNativeIconsVisibleUntilRestart)' in ctl,
 'independent rescue searches true Explorer desktop': 'Progman' in rescue and 'WorkerW' in rescue and 'SHELLDLL_DefView' in rescue and 'SysListView32' in rescue and 'FolderView' in rescue,
 'independent rescue only shows native icon view': 'ShowWindow($found, 5)' in rescue and not any(x in rescue for x in ('Remove-Item','Move-Item','Set-ItemProperty','Stop-Process')),
 'independent rescue packaged and installed': all('recover-desktop-icons.ps1' in source(x) for x in ['package.ps1','install.ps1','uninstall.ps1','apply-update.ps1']),
 'double click hook catches failures and forwards input': 'catch (Exception)' in source('src/CyanDeck/Services/DesktopDoubleClickHook.cs') and 'CallNextHookEx(_hook, nCode, wParam, lParam)' in source('src/CyanDeck/Services/DesktopDoubleClickHook.cs'),
 'gesture hook catches failures and forwards input': 'catch (Exception)' in source('src/CyanDeck/Services/DesktopPageGestureHook.cs') and 'CallNextHookEx(_hook, nCode, wParam, lParam)' in source('src/CyanDeck/Services/DesktopPageGestureHook.cs'),
 'hooks uninstall after first callback failure': all('System.Threading.Interlocked.Exchange(ref _hookFaulted, 1)' in source(x) and 'Dispose(); DiagnosticsLog.Warn' in source(x) for x in ('src/CyanDeck/Services/DesktopDoubleClickHook.cs','src/CyanDeck/Services/DesktopPageGestureHook.cs')),
 'no low level keyboard hook': all('WH_KEYBOARD_LL' not in source(x) for x in ('src/CyanDeck/Services/DesktopDoubleClickHook.cs','src/CyanDeck/Services/DesktopPageGestureHook.cs')),
 'diagnostics summarize pending recovery without exposing private paths': 'Update recovery: pending={UpdateService.HasPendingRecovery()}' in source('src/CyanDeck/Services/DiagnosticBundleService.cs'),
 'shortcut launch does not misstate asynchronous success': 'target launch unverified.' in source('src/CyanDeck/Services/ShortcutLaunchService.cs') and 'ProblemReportService.Reset("shortcut-open:" + shortcut)' not in source('src/CyanDeck/Services/ShortcutLaunchService.cs'),
 'calendar state schema unchanged': 'Current = 35' in source('src/CyanDeck/Services/StateSchema.cs'),
 'secret credential not packaged': not (r/'json/client_secret.json').exists(),
}
for k,v in checks.items(): print(('PASS' if v else 'FAIL'),k)
print(f'v42 recovery regression guards: {sum(checks.values())}/{len(checks)}')
if not all(checks.values()): raise SystemExit(1)
