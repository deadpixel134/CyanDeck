#!/usr/bin/env python3
"""Non-destructive v8.0.28 update failure and shutdown safety regression."""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1]
def read(path): return (root / path).read_text(encoding='utf-8-sig')
app = read('src/CyanDeck/App.xaml.cs')
ctl = read('src/CyanDeck/Services/AppController.cs')
up = read('apply-update.ps1')
policy = read('src/CyanDeck/Services/FailedUpdateRetryPolicy.cs')
testps = read('tools/test_cyandeck_update_package_layout.ps1')
reset_test = read('tests/CompleteResetHarness/Program.cs')
reset_proj = read('tests/CompleteResetHarness/CompleteResetHarness.csproj')
preflight = app.split('private async Task<(bool ExitForUpdate, bool Attempted)> RunStartupUpdatePreflightAsync(',1)[1].split('private static string? GetOptionValue(',1)[0]
exit_block = ctl.split('private void Exit()',1)[1].split('public void Dispose()',1)[0]
dispose = ctl.split('public void Dispose()',1)[1]
checks={
 'signed update check before digest retry suppression':
    preflight.index('var check = await checkTask;') < preflight.index('FailedUpdateRetryPolicy.IsAutomaticRetryDeferred('),
 'skip same package occurs before stage and installer launch':
    preflight.index('FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(') < preflight.index('UpdateService.StagePackageAsync(') and
    preflight.index('FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(') < preflight.index('UpdateService.StartInstall('),
 'automatic retry only after available signed update':
    preflight.index('if (!check.UpdateAvailable)') < preflight.index('FailedUpdateRetryPolicy.IsAutomaticRetryDeferred('),
 'deferred startup opens installed version without restarting installer':
    'manual update remains available' in preflight and 'return (false, true);' in preflight,
 'failure marker scoped to exact 64-digit SHA256':
    "'failed-' + packageSha256.ToLowerInvariant() + \".txt\"" in policy and
    'packageSha256.Length != 64' in policy and 'packageSha256.All(Uri.IsHexDigit)' in policy,
 '12 hour backoff with stale and future marker expiry':
    'TimeSpan.FromHours(12)' in policy and
    'elapsed >= TimeSpan.Zero && elapsed < RetryDelay' in policy,
 'read failure marker without writing to roaming data':
    'IsAutomaticRetryDeferred(' in policy and 'FileInfo(path)' in policy and
    all(z not in policy for z in ('File.WriteAllText(', 'Directory.Delete(', 'File.Delete(', 'DataRootPaths.ActiveRoaming')),
 'symlinked marker not used for startup decision':
    '(info.Attributes & FileAttributes.ReparsePoint) != 0' in policy,
 'updater writes marker only for package failures':
    up.count("$ExpectedSha256 -match '^[a-fA-F0-9]{64}$'") == 2 and
    '$Package -and -not $RecoverPending -and -not $RollbackDir' in up,
 'failed digest marker written alongside existing error log':
    "'last-update-error.log'" in up and
    '[IO.File]::WriteAllText($failureMarker,' in up,
 'successful update clears same digest marker':
    'Remove-Item -LiteralPath $failureMarker -Force' in up and
    '$updateProgressSucceeded = $true' in up,
 'rollback and pending recovery journal preserved':
    'Restore-PendingUpdate $journal' in up and 'pending-$key.json' in up and
    'if (Test-Path -LiteralPath $journal)' in up,
 'failed installer does not automatically relaunch with pending recovery journal':
    '(-not $journal -or -not (Test-Path -LiteralPath $journal))' in up,
 'updater never destroys installation folder':
    'Remove-Item -LiteralPath $InstallDir -Recurse' not in up,
 'shutdown checks display transition before destroying bridge':
    exit_block.index('_exitDuringDisplayTransition = _displayTransition?.IsStableForUserLayoutAction == false;') <
    exit_block.index('_displayTransition?.Dispose();'),
 'exit with changing monitors keeps last saved layout':
    'if (displayTransitionInProgress)' in dispose and
    'temporary positions not persisted' in dispose,
 'final normal shutdown demands durable checkpoint':
    'PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true)' in dispose,
 'failed shutdown save retains session recovery marker':
    'shutdownSaveSucceeded = false;' in dispose and
    'if (!_abnormalTermination && shutdownSaveSucceeded)' in dispose and
    '_store.EndSession();' in dispose,
 'PowerShell disposable package tests exercise both failure and success marker':
    'Rejected package did not record its exact failed digest' in testps and
    'Successful update failed to remove the exact package retry marker.' in testps,
 'C# disposable local root tests cover backoff and corrected release digest':
    'mockLocal = Path.Combine(parent, "local")' in reset_test and
    'differentSha = new string(' in reset_test and
    'Expired failure marker prevented the 12-hour retry.' in reset_test,
 'policy included in built Windows .NET harness':
    'FailedUpdateRetryPolicy.cs' in reset_proj,
 'new version with no calendar state schema change':
    tuple(map(int,re.search(r'<Version>(\d+\.\d+\.\d+)</Version>',read('src/CyanDeck/CyanDeck.csproj')).group(1).split('.'))) >= (8,0,28) and
    'Current = 35' in read('src/CyanDeck/Services/StateSchema.cs')
}
for label,passed in checks.items():print(('PASS' if passed else 'FAIL') + ': ' + label)
print(f'RESULT {sum(checks.values())}/{len(checks)}')
if not all(checks.values()):raise SystemExit(1)
