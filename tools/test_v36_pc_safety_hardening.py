#!/usr/bin/env python3
"""Non-destructive source guards for v36 safety mitigation. No Windows side-effects."""
from pathlib import Path
root = Path(__file__).resolve().parents[1]
def read(rel): return (root/rel).read_text(encoding='utf-8-sig')
up = read('apply-update.ps1')
svc = read('src/CyanDeck/Services/UpdateService.cs')
ctl = read('src/CyanDeck/Services/AppController.cs')
fence = read('src/CyanDeck/Views/FenceWindow.xaml.cs')
diag = read('src/CyanDeck/Services/DiagnosticBundleService.cs')
inst = read('install.ps1')
uninst = read('uninstall.ps1')
build = read('build.ps1')
package = read('package.ps1')
model = read('src/CyanDeck/Models/Models.cs')
xaml = read('src/CyanDeck/Views/SettingsWindow.xaml')
checks = {
    'no indiscriminate updater install dir wipe': 'Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue | Remove-Item' not in up,
    'updater has only explicit owned file list': '$managedNames = @(' in up and "'CyanDeck.exe'" in up,
    'only managed items may be replaced': 'if ($Name -cnotin $managedNames)' in up,
    'atomic replacement of existing app-owned leaf file': '[IO.File]::Replace($staging, $target, $replaceBackup)' in up and '.CyanDeck-replace-' in up,
    'updater snapshot is application-owned only': 'function Copy-ManagedSnapshot' in up and 'Copy-ManagedSnapshot $InstallDir $safety' in up,
    'rollback must use app snapshot': 'Rollback source must be an application-owned snapshot' in up,
    'updater never force-kills process': 'Stop-Process -Force' not in up,
    'updater aborts on busy app': 'update cancelled with existing files intact' in up,
    'updater requires trusted package SHA': "ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$'" in up,
    'updater checks package hash before expansion': up.index('Get-FileHash -LiteralPath $Package') < up.index('Expand-Archive'),
    'updater prevalidates ZIP path components': "'Unsafe path in update ZIP.'" in up,
    'updater prevents zip bomb size': 'expanded size limit exceeded' in up,
    'C# checks always require verified manifest': 'VerifyManifestSignature(manifest, true' in svc,
    'C# stage rejects unsigned package': 'if (!check.SignatureVerified)' in svc,
    'C# rejects HTTP manifest and package': 'Insecure HTTP update sources are forbidden' in svc and 'HTTP update packages are forbidden' in svc,
    'HTTPS never redirects to HTTP': 'AllowAutoRedirect = false' in svc and 'response.EnsureSuccessStatusCode()' in svc,
    'C# passes hash to install': '-ExpectedSha256 {Quote(expectedSha256)}' in svc,
    'controller forwards signed hash': 'TryLaunchInstall(staged, check.Manifest.Sha256' in ctl,
    'legacy settings default signed': 'RequireSignedUpdateManifests { get; set; } = true;' in model,
    'signing cannot be disabled in UI': 'IsEnabled="False" IsChecked="True"' in xaml,
    'portal prevents source-in-destination cycle': 'destination.StartsWith(sourceFull.TrimEnd(Path.DirectorySeparatorChar)' in fence,
    'portal prevents protected destination': 'if (IsProtectedDeletionTarget(destination))' in fence,
    'portal protects system subtree': 'var protectedTrees = new[]' in fence and 'full.StartsWith(Path.GetFullPath(folder)' in fence,
    'portal prevents reparse ancestors': 'var current = full;' in fence and 'File.GetAttributes(current)' in fence,
    'portal prevents symlink file deep-copy': 'Reparse-point files are not copied recursively' in fence,
    'diagnostic ZIP does not predelete destination': 'File.Delete(destinationPath)' not in diag,
    'diagnostic ZIP uses atomic replacement': 'File.Replace(tempZip, destinationPath, null)' in diag,
    'diagnostic ZIP masks secrets and profile': 'WriteRedactedLog' in diag and 'refresh_token' in diag and 'Regex.Escape(profile)' in diag,
    'install no force-kill': 'Stop-Process -Force' not in inst,
    'uninstall no force-kill': 'Stop-Process -Force' not in uninst,
    'uninstall no recursive install dir removal': "Remove-Item $installDir -Recurse -Force" not in uninst,
    'uninstall explicit data purge confirmation': '[switch]$ConfirmPurgeData' in uninst and 'if ($PurgeData -and -not $ConfirmPurgeData)' in uninst,
    'uninstall checks junctions without recursive traversal': 'Stack[string]' in uninst and 'Refusing to purge a data directory containing junctions or symbolic links.' in uninst,
    'uninstall checks managed install link': 'Uninstallation refused: installation directory is a junction or symbolic link.' in uninst,
    'uninstall user data backed up before purge': 'User data safety backup:' in uninst,
    'uninstall leaves unrelated shortcuts intact': '$link.TargetPath' in uninst,
    'build output not recursively cleaned': 'Remove-Item -Recurse -Force $out' not in build,
    'package staging guarded from arbitrary deletion': '$unknown.Count -gt 0' in package and 'Remove-Item $stage -Recurse -Force' not in package,
    'release packaging requires a signing key': 'Production release packaging requires -SigningKeyPath' in package,
    'release verifier rejects unsigned manifest': 'Unsigned update manifest is not permitted' in read('verify-release.ps1'),
    'schema is preserved': 'Current = 35' in read('src/CyanDeck/Services/StateSchema.cs'),
    'desktop collection Delete protected': 'CollectionDesktopDeleteDialog.Show(this, tiles.Count)' in fence,
    'local Google events protected': '!x.LocalOnly && !x.Conflict && !x.RemoteReadOnly' in read('src/CyanDeck/Calendar/GoogleCalendarSync.cs'),
    'Google OAuth user secret not bundled': not (root/'json/client_secret.json').exists(),
}
for name, passed in checks.items():
    print(('PASS' if passed else 'FAIL') + ': ' + name)
print(f'v36 PC safety source guards: {sum(checks.values())}/{len(checks)}')
if not all(checks.values()):
    raise SystemExit(1)
