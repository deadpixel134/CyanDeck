param(
  [string]$Package,
  [string]$RollbackDir,
  [Parameter(Mandatory=$true)][string]$InstallDir,
  [string]$ExpectedSha256,
  [int]$WaitForPid = 0,
  [switch]$Relaunch,
  [switch]$RecoverPending
)

$ErrorActionPreference = "Stop"
# SAFETY: Never remove the contents of InstallDir. Portable users can keep other
# files alongside CyanDeck.exe. Only these application-owned leaf files may
# be backed up or replaced by the updater/rollback.
$managedNames = @(
  'CyanDeck.exe', 'apply-update.ps1','recover-desktop-icons.ps1', 'install.ps1', 'uninstall.ps1',
  'verify-release.ps1', 'cyandeck-google-client-id.txt',
  'CyanDeck.update-public-key.pem'
)

function Assert-OrdinaryDirectory([string]$Path, [string]$Label) {
  $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
  $root = [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')
  if (-not $full -or $full -eq $root) { throw "$Label cannot be a drive root." }
  if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "$Label not found: $full" }
  $cursor = $full
  while ($cursor) {
    $folder = Get-Item -LiteralPath $cursor -Force
    if (($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "$Label path must not traverse a junction or symbolic link: $cursor"
    }
    $parent = Split-Path -Parent $cursor
    if (-not $parent -or $parent -eq $cursor) { break }
    $cursor = $parent
  }
  return $full
}

function Assert-ManagedFile([string]$Dir, [string]$Name) {
  if ($Name -cnotin $managedNames) { throw "Non-application file denied: $Name" }
  $path = Join-Path $Dir $Name
  if (Test-Path -LiteralPath $path) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "A managed file path is a directory or reparse point: $path"
    }
  }
  return $path
}

# Persisted, per-install recovery record. Never trust an arbitrary path from it.
# The record is written atomically BEFORE the first application file is replaced.
function Get-RecoveryJournal([string]$Directory) {
  $normalized = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/').ToUpperInvariant()
  $sha = [Security.Cryptography.SHA256]::Create()
  try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalized)) }
  finally { $sha.Dispose() }
  $key = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
  return (Join-Path $updatesRoot ("pending-$key.json"))
}

function Write-RecoveryJournal([string]$Journal, [string]$Snapshot) {
  $payload = @{ installDir=$InstallDir; snapshot=$Snapshot; createdUtc=[DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json -Compress
  $temporary = $Journal + '.new-' + [Guid]::NewGuid().ToString('N')
  try {
    [IO.File]::WriteAllText($temporary, $payload, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Journal) { throw 'An unresolved update recovery journal already exists.' }
    [IO.File]::Move($temporary, $Journal)
  } finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
  }
}

function Restore-PendingUpdate([string]$Journal) {
  if (-not (Test-Path -LiteralPath $Journal -PathType Leaf)) {
    throw 'No pending update recovery was found for this installation.'
  }
  $entry = Get-Item -LiteralPath $Journal -Force
  if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Recovery journal must not be a symbolic link.'
  }
  $record = Get-Content -LiteralPath $Journal -Raw | ConvertFrom-Json
  if (-not [string]::Equals($record.installDir, $InstallDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Recovery journal installation path mismatch.'
  }
  $snapshot = Assert-OrdinaryDirectory $record.snapshot 'Recovery snapshot'
  $root = [IO.Path]::GetFullPath($rollbackRoot).TrimEnd('\','/')
  if (-not $snapshot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Recovery snapshot is outside CyanDeck-owned backup storage.'
  }
  $originalExe = Assert-ManagedFile $snapshot 'CyanDeck.exe'
  if (-not (Test-Path -LiteralPath $originalExe -PathType Leaf)) {
    throw 'Recovery snapshot is missing CyanDeck.exe; no installation files were changed.'
  }
  Install-ManagedFiles $snapshot $InstallDir
  foreach ($name in $managedNames) {
    $source = Assert-ManagedFile $snapshot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $target = Assert-ManagedFile $InstallDir $name
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
      throw "Recovery verification failed for $name; journal preserved."
    }
  }
  # Never remove new files that were absent from the original snapshot: the
  # updater should not destroy a user-created file even with a managed name.
  Remove-Item -LiteralPath $Journal -Force
  Write-Host 'CyanDeck recovery verified. Original managed files restored; other files untouched.'
}

function Wait-CyanDeckProcess([int]$ProcessId) {
  if ($ProcessId -le 0) { return }
  try { $p = Get-Process -Id $ProcessId -ErrorAction Stop }
  catch { return }
  if (-not $p.WaitForExit(30000)) {
    # Never force-kill the app while it may be saving its layout/calendar.
    throw 'CyanDeck did not finish closing; update cancelled with existing files intact.'
  }
}

function Copy-ManagedSnapshot([string]$SourceDir, [string]$DestinationDir) {
  New-Item -ItemType Directory -Path $DestinationDir -ErrorAction Stop | Out-Null
  foreach ($name in $managedNames) {
    $src = Assert-ManagedFile $SourceDir $name
    if (Test-Path -LiteralPath $src -PathType Leaf) {
      [IO.File]::Copy($src, (Join-Path $DestinationDir $name), $false)
    }
  }
}

# Abort before touching installed application files if the safety snapshot is
# incomplete. This is a precondition, not merely best-effort rollback.
function Assert-SnapshotMatches([string]$OriginalDir, [string]$SnapshotDir) {
  foreach ($name in $managedNames) {
    $original = Assert-ManagedFile $OriginalDir $name
    $copy = Assert-ManagedFile $SnapshotDir $name
    if (-not (Test-Path -LiteralPath $original -PathType Leaf)) { continue }
    if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
        (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ine
        (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) {
      throw "Update stopped: safety snapshot could not be verified for $name."
    }
  }
}

function Install-ManagedFiles([string]$SourceDir, [string]$DestinationDir) {
  foreach ($name in $managedNames) {
    $source = Assert-ManagedFile $SourceDir $name
    $target = Assert-ManagedFile $DestinationDir $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $staging = Join-Path $DestinationDir ('.CyanDeck-stage-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
      [IO.File]::Copy($source, $staging, $false)
      if (Test-Path -LiteralPath $target -PathType Leaf) {
        # .NET Framework/PowerShell 5.1 may marshal a null backup path as
        # empty. File.Replace then fails AFTER the recovery journal exists.
        # Use a unique, app-owned NTFS backup name and remove it after a
        # verified atomic replacement; the durable snapshot is elsewhere.
        $replaceBackup = Join-Path $DestinationDir ('.CyanDeck-replace-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
          [IO.File]::Replace($staging, $target, $replaceBackup)
        } finally {
          if (Test-Path -LiteralPath $replaceBackup) {
            Remove-Item -LiteralPath $replaceBackup -Force -ErrorAction Stop
          }
        }
      } else {
        [IO.File]::Move($staging, $target)
      }
    } finally {
      if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force }
    }
  }
}

# This legacy-compatible updater must never reinstall or recover an old EXE
# after a future CyanDeck data cutover. Treat any existing fence (including a
# directory, malformed file, or symlink) as blocking until a compatible updater
# explicitly handles the new storage contract.
# Wait until the requesting application has exited before taking both locks.
# Keep both owned until installation/rollback/recovery finishes, then relaunch.
Wait-CyanDeckProcess $WaitForPid
$cyanUpdateMutex = [Threading.Mutex]::new($false, 'Local\CyanDeck.SingleInstance')
$ownsCyanUpdateMutex = $false
try {
  try { $ownsCyanUpdateMutex = $cyanUpdateMutex.WaitOne(0) }
  catch [Threading.AbandonedMutexException] {
    $ownsCyanUpdateMutex = $true
    throw 'CyanDeck stopped unexpectedly; inspect its data before updating or recovering.'
  }
  if (-not $ownsCyanUpdateMutex) {
    throw 'CyanDeck is running or another update owns its profile. Update cancelled.'
  }
# Manual recovery must not replace files while another CyanDeck instance is running.
if (@(Get-Process -Name CyanDeck -ErrorAction SilentlyContinue).Count -gt 0) {
  throw 'CyanDeck is still running. Exit all instances normally before updating or recovering.'
}
$InstallDir = Assert-OrdinaryDirectory $InstallDir 'Install directory'
$installedExe = Assert-ManagedFile $InstallDir 'CyanDeck.exe'
if (-not $RecoverPending -and -not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
  throw 'CyanDeck.exe was not found in the installation directory; update cancelled.'
}
$updatesRoot = Join-Path $env:LOCALAPPDATA 'CyanDeck\updates'
$rollbackRoot = Join-Path $updatesRoot 'rollback'
New-Item -ItemType Directory -Path $rollbackRoot -Force | Out-Null
$rollbackRoot = Assert-OrdinaryDirectory $rollbackRoot 'Rollback storage'
$journal = Get-RecoveryJournal $InstallDir
if ($RecoverPending) {
  Restore-PendingUpdate $journal
} else {
if (Test-Path -LiteralPath $journal) {
  throw "Previous update has an unresolved recovery journal: $journal. Run apply-update.ps1 -RecoverPending -InstallDir <directory> after closing CyanDeck."
}
$marker = Join-Path $updatesRoot 'last-rollback.txt'
$tempRoot = Join-Path $env:TEMP ('CyanDeck-update-' + [Guid]::NewGuid().ToString('N'))
$operation = if ($RollbackDir) { 'rollback' } else { 'update' }
$safety = $null
$changed = $false

try {
  if ($RollbackDir) {
    $RollbackDir = Assert-OrdinaryDirectory $RollbackDir 'Rollback directory'
    $rollbackRootFull = [IO.Path]::GetFullPath($rollbackRoot).TrimEnd('\', '/')
    if (-not $RollbackDir.StartsWith($rollbackRootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
      throw 'Rollback source must be an application-owned snapshot.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $RollbackDir 'CyanDeck.exe') -PathType Leaf)) {
      throw 'The rollback snapshot is missing CyanDeck.exe.'
    }
    $safety = Join-Path $rollbackRoot ('pre-rollback-' + [Guid]::NewGuid().ToString('N'))
    Copy-ManagedSnapshot $InstallDir $safety
    Assert-SnapshotMatches $InstallDir $safety
    Write-RecoveryJournal $journal $safety
    $changed = $true
    Install-ManagedFiles $RollbackDir $InstallDir
    [IO.File]::WriteAllText($marker, $safety)
    Remove-Item -LiteralPath $journal -Force
  } else {
    if (-not $Package -or -not (Test-Path -LiteralPath $Package -PathType Leaf)) {
      throw 'Update package was not found.'
    }
    if ($ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$') {
      throw 'A verified SHA-256 package digest is required to install an update.'
    }
    $actualHash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash
    if ($actualHash -ine $ExpectedSha256) { throw 'Staged update package SHA-256 mismatch. Update cancelled.' }

    # Validate every ZIP entry before extraction: exactly one flat payload or
    # one wrapped payload directory. Ignoring sibling roots would allow a
    # signed release to conceal extra files beyond the installed EXE folder.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Package)
    $payloadPrefix = $null
    $rootFiles = $false
    $declaredDirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $seenEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $allowedZipFiles = @($managedNames) + @('README.md')
    try {
      if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 35) {
        throw 'Unexpected update ZIP file table size.'
      }
      $totalSize = [long]0
      $exeCount = 0
      foreach ($entry in $archive.Entries) {
        $relative = $entry.FullName.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative.StartsWith('/') -or $relative.Contains(':') -or
            $relative -match '[\x00-\x1f<>|?*]') {
          throw 'Unsafe path in update ZIP.'
        }
        $isDir = $relative.EndsWith('/')
        $parts = @($relative.TrimEnd('/').Split('/'))
        if ($parts.Count -gt 2 -or $parts.Count -eq 0 -or
            @($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0 -or
            ($isDir -and $parts.Count -ne 1)) {
          throw 'Unexpected path depth in update ZIP.'
        }
        if (-not $seenEntries.Add($relative.TrimEnd('/'))) {
          throw 'Duplicate update ZIP entry (case-insensitive).'
        }
        $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
        $windowsAttrs = (($entry.ExternalAttributes -shr 16) -band 0xFFFF)
        if ($unixType -eq 0xA000 -or ($windowsAttrs -band [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
          throw 'Update ZIP includes a link.'
        }
        if ($isDir) {
          if ($entry.Length -ne 0 -or $parts[0] -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
            throw 'Unsafe update ZIP directory.'
          }
          [void]$declaredDirs.Add($parts[0])
          continue
        }
        $name = $parts[-1]
        if ($name -cnotin $allowedZipFiles) { throw 'Update ZIP includes a non-application file.' }
        if ($parts.Count -eq 1) {
          $rootFiles = $true
        } else {
          if ($parts[0] -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
            throw 'Unsafe update ZIP root directory.'
          }
          if ($null -eq $payloadPrefix) { $payloadPrefix = $parts[0] }
          elseif (-not [string]::Equals($payloadPrefix, $parts[0], [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Update ZIP contains multiple payload directories.'
          }
        }
        if ($name -ceq 'CyanDeck.exe') { $exeCount++ }
        if ($entry.Length -lt 0 -or $entry.Length -gt 1000000000 -or
            $totalSize -gt (1500000000 - $entry.Length)) {
          throw 'Update ZIP expanded size limit exceeded.'
        }
        $totalSize += $entry.Length
      }
      if ($exeCount -ne 1) { throw 'The package must contain exactly one CyanDeck.exe.' }
      if ($rootFiles -and $null -ne $payloadPrefix) {
        throw 'Update ZIP mixes root files and a nested payload.'
      }
      if ($declaredDirs.Count -gt 0 -and ($null -eq $payloadPrefix -or
          $declaredDirs.Count -ne 1 -or -not $declaredDirs.Contains($payloadPrefix))) {
        throw 'Update ZIP has an unexpected directory.'
      }
    } finally { $archive.Dispose() }
    New-Item -ItemType Directory -Path $tempRoot -ErrorAction Stop | Out-Null
    Expand-Archive -LiteralPath $Package -DestinationPath $tempRoot -ErrorAction Stop
    $exes = @(Get-ChildItem -LiteralPath $tempRoot -Filter CyanDeck.exe -File -Recurse)
    if ($exes.Count -ne 1) { throw 'The package must contain exactly one CyanDeck.exe.' }
    $payloadDir = if ($null -ne $payloadPrefix) { Join-Path $tempRoot $payloadPrefix } else { $tempRoot }
    # Ensure extraction produced exactly the validated root; reject ignored
    # sibling directories and reparse points, not just files inside payloadDir.
    foreach ($item in (Get-ChildItem -LiteralPath $tempRoot -Force)) {
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Update ZIP extraction produced a link.'
      }
      if ($null -ne $payloadPrefix -and
          ($item.Name -cne $payloadPrefix -or -not $item.PSIsContainer)) {
        throw 'Update ZIP extraction produced an unexpected sibling.'
      }
      if ($null -eq $payloadPrefix -and $item.PSIsContainer) {
        throw 'Update ZIP extraction produced an unexpected directory.'
      }
    }
    # Use FileInfo.DirectoryName directly: command-mode Split-Path can treat
    # a bare indexed member expression as a literal argument.
    $actualExeDirectory = $exes[0].DirectoryName
    if (-not [string]::Equals([IO.Path]::GetFullPath($payloadDir),
        [IO.Path]::GetFullPath($actualExeDirectory), [StringComparison]::OrdinalIgnoreCase)) {
      throw 'Update ZIP executable escaped the selected payload root.'
    }
    # Fail closed: no unrecognized content or nested directories in the payload.
    foreach ($item in (Get-ChildItem -LiteralPath $payloadDir -Force)) {
      if ($item.PSIsContainer -or $item.Name -cnotin $managedNames -and $item.Name -ne 'README.md') {
        throw "Unexpected update package item: $($item.Name)"
      }
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Package contains a reparse point.'
      }
    }
    $safety = Join-Path $rollbackRoot ('before-update-' + [Guid]::NewGuid().ToString('N'))
    Copy-ManagedSnapshot $InstallDir $safety
    Assert-SnapshotMatches $InstallDir $safety
    Write-RecoveryJournal $journal $safety
    $changed = $true
    Install-ManagedFiles $payloadDir $InstallDir
    [IO.File]::WriteAllText($marker, $safety)
    Remove-Item -LiteralPath $journal -Force
  }
} catch {
  $failure = $_
  if ($changed -and (Test-Path -LiteralPath $journal)) {
    try {
      Restore-PendingUpdate $journal
      Write-Warning 'Update failed; previous managed files restored and verified.'
    } catch {
      Write-Warning ('Update failed; automatic recovery could not complete. Backup and journal retained: ' + $_.Exception.Message)
    }
  }
  Write-Error $failure
  exit 1
} finally {
  if (Test-Path -LiteralPath $tempRoot) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
}
} # Completed normal update/rollback branch.
} finally {
  if ($ownsCyanUpdateMutex) { $cyanUpdateMutex.ReleaseMutex() }
  $cyanUpdateMutex.Dispose()
}

# A relaunched EXE must not race its own updater's still-held mutex.
if ($Relaunch) {
  $exe = Join-Path $InstallDir 'CyanDeck.exe'
  if (Test-Path -LiteralPath $exe -PathType Leaf) { Start-Process -FilePath $exe }
}
