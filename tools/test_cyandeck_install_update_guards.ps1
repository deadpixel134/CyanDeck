# Non-destructive Windows integration test. All application/profile paths
# resolve inside a newly created disposable directory, never the user's data.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-distribution-guard-' + [Guid]::NewGuid().ToString('N'))
$oldAppData = $env:APPDATA
$oldLocalAppData = $env:LOCALAPPDATA
try {
  $env:APPDATA = Join-Path $root 'roaming'
  $env:LOCALAPPDATA = Join-Path $root 'local'
  $installerDir = Join-Path $root 'installer-payload'
  $installedDir = Join-Path $root 'already-installed'
  New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
  New-Item -ItemType Directory -Path $installedDir -Force | Out-Null
  $installer = Join-Path $installerDir 'install.ps1'
  $updater = Join-Path $installerDir 'apply-update.ps1'
  $uninstaller = Join-Path $installerDir 'uninstall.ps1'
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../install.ps1') -Destination $installer
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../apply-update.ps1') -Destination $updater
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../uninstall.ps1') -Destination $uninstaller
  # Keep the real uninstaller script above; do not overwrite it with a stub.
  foreach ($filename in @('DesktopDeck.exe', 'recover-desktop-icons.ps1')) {
    Set-Content -LiteralPath (Join-Path $installerDir $filename) -Value 'test-only payload' -Encoding Ascii
  }
  Set-Content -LiteralPath (Join-Path $installedDir 'DesktopDeck.exe') -Value 'test-only installed EXE' -Encoding Ascii
  $managedInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\DesktopDeck'
  $fenceDir = Join-Path $env:LOCALAPPDATA 'DesktopDeck\migration'
  $fencePath = Join-Path $fenceDir 'legacy-writer-fenced.json'
  New-Item -ItemType Directory -Path $fenceDir -Force | Out-Null

  function Assert-Blocked([scriptblock]$Action, [string]$Expected, [string]$Label) {
    $blocked = $false
    $actual = '<no exception>'
    try { & $Action | Out-Null }
    catch {
      $actual = [string]$_
      $blocked = $actual.Contains($Expected)
    }
    if (-not $blocked) { throw "Guard did not block $Label. Expected: $Expected. Actual: $actual" }
    if (Test-Path -LiteralPath $managedInstallDir) {
      throw "Guard modified installed application directory during $Label"
    }
  }

  # An active cutover sentinel blocks both reinstall AND updater rollback or
  # crash recovery, even when the file body is not parseable JSON.
  Set-Content -LiteralPath $fencePath -Value '{bad-json' -Encoding Ascii
  Assert-Blocked { & $installer } 'cutover fence is present' 'installer with cutover fence'
  Assert-Blocked { & $uninstaller -PurgeData -ConfirmPurgeData } 'cutover fence is present' 'uninstaller with cutover fence'
  Assert-Blocked { & $updater -InstallDir $installedDir -RecoverPending } 'cutover fence is present' 'recovery with cutover fence'
  Assert-Blocked { & $updater -InstallDir $installedDir -RollbackDir (Join-Path $root 'not-used') } 'cutover fence is present' 'rollback with cutover fence'
  Assert-Blocked { & $updater -InstallDir $installedDir -Package (Join-Path $root 'not-used.zip') } 'cutover fence is present' 'update with cutover fence'
  Remove-Item -LiteralPath $fencePath -Force

  # Directories at the fence name must also block; Test-Path -PathType Leaf
  # would incorrectly treat these as a missing marker.
  New-Item -ItemType Directory -Path $fencePath | Out-Null
  Assert-Blocked { & $installer } 'cutover fence is present' 'installer with directory fence'
  Assert-Blocked { & $uninstaller -PurgeData -ConfirmPurgeData } 'cutover fence is present' 'uninstaller with directory fence'
  Assert-Blocked { & $updater -InstallDir $installedDir -RecoverPending } 'cutover fence is present' 'updater with directory fence'
  Remove-Item -LiteralPath $fencePath

  # A stale running old binary owns the original singleton mutex; a new
  # CyanDeck binary can own the cross-executable writer lease. Each must
  # independently prevent any installer or updater write.
  foreach ($mutexName in @('Local\DesktopDeck.SingleInstance', 'Local\CyanDeck.LegacyProfileWriter.v1')) {
    $mutex = [Threading.Mutex]::new($false, $mutexName)
    $owned = $false
    try {
      $owned = $mutex.WaitOne(0)
      if (-not $owned) { throw "Test environment unexpectedly holds $mutexName" }
      foreach ($target in @('install', 'update', 'uninstall')) {
        $job = Start-Job -ScriptBlock {
          param($installerPath, $updaterPath, $uninstallerPath, $appData, $localAppData, $installDir, $kind)
          $env:APPDATA = $appData
          $env:LOCALAPPDATA = $localAppData
          try {
            if ($kind -eq 'install') { & $installerPath | Out-Null }
            elseif ($kind -eq 'uninstall') { & $uninstallerPath -PurgeData -ConfirmPurgeData | Out-Null }
            else { & $updaterPath -InstallDir $installDir -RecoverPending | Out-Null }
            'UNBLOCKED'
          } catch { 'BLOCKED:' + $_.Exception.Message }
        } -ArgumentList $installer, $updater, $uninstaller, $env:APPDATA, $env:LOCALAPPDATA, $installedDir, $target
        try {
          if (-not (Wait-Job -Job $job -Timeout 30)) {
            Stop-Job -Job $job
            throw "Process contention test timed out for $target on $mutexName"
          }
          $output = @(Receive-Job -Job $job -ErrorAction Stop)
          $isLegacy = $mutexName -eq 'Local\DesktopDeck.SingleInstance'
          $expected = if ($isLegacy) {
            if ($target -eq 'install') { 'DesktopDeck is running or another installer is active.' }
            elseif ($target -eq 'uninstall') { 'DesktopDeck is running or another profile operation is active.' }
            else { 'DesktopDeck is running or another update owns its profile.' }
          } else {
            if ($target -eq 'install') { 'Another CyanDeck writer or data migration holds the legacy profile.' }
            elseif ($target -eq 'uninstall') { 'Another CyanDeck writer or data migration owns the profile.' }
            else { 'A CyanDeck writer or data migration owns the legacy profile.' }
          }
          if ($output.Count -ne 1 -or
              -not ([string]$output[0]).StartsWith('BLOCKED:') -or
              -not ([string]$output[0]).Contains($expected)) {
            throw "Did not block $target for the expected mutex reason when $mutexName was held: $($output -join '; ')"
          }
        } finally { Remove-Job -Job $job -Force }
      }
    } finally {
      if ($owned) { $mutex.ReleaseMutex() }
      $mutex.Dispose()
    }
  }
  # Purging the old profile alone while a prepared new copy exists would
  # orphan the latter (which may contain Google tokens). Fail before backup/delete.
  $legacyProfile = Join-Path $env:APPDATA 'DesktopDeck'
  $cyanProfile = Join-Path $env:APPDATA 'CyanDeck'
  New-Item -ItemType Directory -Path $legacyProfile -Force | Out-Null
  Set-Content -LiteralPath (Join-Path $legacyProfile 'layout.json') -Value '{"Version":35}' -Encoding Ascii
  New-Item -ItemType Directory -Path $cyanProfile -Force | Out-Null
  Set-Content -LiteralPath (Join-Path $cyanProfile 'layout.json') -Value '{"Version":35}' -Encoding Ascii
  Assert-Blocked { & $uninstaller -PurgeData -ConfirmPurgeData } 'candidate profile exists' 'uninstaller with prepared copy'
  if (-not (Test-Path -LiteralPath (Join-Path $legacyProfile 'layout.json')) -or
      -not (Test-Path -LiteralPath (Join-Path $cyanProfile 'layout.json'))) {
    throw 'Partial purge removed a profile even though a prepared copy existed.'
  }
  if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'DesktopDeck-uninstall-backups')) {
    throw 'Blocked partial purge unexpectedly created a credential-containing backup.'
  }
  if (Test-Path -LiteralPath $managedInstallDir) {
    throw 'Blocked distribution operations unexpectedly created an installation.'
  }
  if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'DesktopDeck\updates')) {
    throw 'Blocked updater unexpectedly created a recovery or rollback directory.'
  }
  if ((Get-Content -LiteralPath (Join-Path $installedDir 'DesktopDeck.exe') -Raw).Trim() -ne 'test-only installed EXE') {
    throw 'Blocked updater altered an existing installed executable.'
  }
  Write-Host 'CyanDeck installer/updater cutover and mutex guards passed.'
}
finally {
  $env:APPDATA = $oldAppData
  $env:LOCALAPPDATA = $oldLocalAppData
  if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
  }
}
