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
# files alongside DesktopDeck.exe. Only these application-owned leaf files may
# be backed up or replaced by the updater/rollback.
$managedNames = @(
  'DesktopDeck.exe', 'apply-update.ps1','recover-desktop-icons.ps1', 'install.ps1', 'uninstall.ps1',
  'verify-release.ps1', 'desktopdeck-google-client-id.txt',
  'DesktopDeck.update-public-key.pem'
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
    throw 'Recovery snapshot is outside DesktopDeck-owned backup storage.'
  }
  $originalExe = Assert-ManagedFile $snapshot 'DesktopDeck.exe'
  if (-not (Test-Path -LiteralPath $originalExe -PathType Leaf)) {
    throw 'Recovery snapshot is missing DesktopDeck.exe; no installation files were changed.'
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
  Write-Host 'DesktopDeck recovery verified. Original managed files restored; other files untouched.'
}

function Wait-DesktopDeckProcess([int]$ProcessId) {
  if ($ProcessId -le 0) { return }
  try { $p = Get-Process -Id $ProcessId -ErrorAction Stop }
  catch { return }
  if (-not $p.WaitForExit(30000)) {
    # Never force-kill the app while it may be saving its layout/calendar.
    throw 'DesktopDeck did not finish closing; update cancelled with existing files intact.'
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
    $staging = Join-Path $DestinationDir ('.DesktopDeck-stage-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
      [IO.File]::Copy($source, $staging, $false)
      if (Test-Path -LiteralPath $target -PathType Leaf) {
        # NTFS atomic replace; permission/lock failure aborts rather than delete.
        [IO.File]::Replace($staging, $target, $null)
      } else {
        [IO.File]::Move($staging, $target)
      }
    } finally {
      if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force }
    }
  }
}

Wait-DesktopDeckProcess $WaitForPid
# Manual recovery must not replace files while another DesktopDeck instance is running.
if (@(Get-Process -Name DesktopDeck -ErrorAction SilentlyContinue).Count -gt 0) {
  throw 'DesktopDeck is still running. Exit all instances normally before updating or recovering.'
}
$InstallDir = Assert-OrdinaryDirectory $InstallDir 'Install directory'
$installedExe = Assert-ManagedFile $InstallDir 'DesktopDeck.exe'
if (-not $RecoverPending -and -not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
  throw 'DesktopDeck.exe was not found in the installation directory; update cancelled.'
}
$updatesRoot = Join-Path $env:LOCALAPPDATA 'DesktopDeck\updates'
$rollbackRoot = Join-Path $updatesRoot 'rollback'
New-Item -ItemType Directory -Path $rollbackRoot -Force | Out-Null
$rollbackRoot = Assert-OrdinaryDirectory $rollbackRoot 'Rollback storage'
$journal = Get-RecoveryJournal $InstallDir
if ($RecoverPending) {
  Restore-PendingUpdate $journal
  if ($Relaunch) { Start-Process -FilePath $installedExe }
  exit 0
}
if (Test-Path -LiteralPath $journal) {
  throw "Previous update has an unresolved recovery journal: $journal. Run apply-update.ps1 -RecoverPending -InstallDir <directory> after closing DesktopDeck."
}
$marker = Join-Path $updatesRoot 'last-rollback.txt'
$tempRoot = Join-Path $env:TEMP ('DesktopDeck-update-' + [Guid]::NewGuid().ToString('N'))
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
    if (-not (Test-Path -LiteralPath (Join-Path $RollbackDir 'DesktopDeck.exe') -PathType Leaf)) {
      throw 'The rollback snapshot is missing DesktopDeck.exe.'
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

    # Validate all ZIP entry paths BEFORE extraction. Reject traversal,
    # device/absolute paths, unknown files, deep trees and oversized archives.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
      if ($archive.Entries.Count -gt 35) { throw 'Unexpectedly large update ZIP file table.' }
      $totalSize = [long]0
      foreach ($entry in $archive.Entries) {
        $relative = $entry.FullName.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative.StartsWith('/') -or $relative.Contains(':') -or
            $relative -match '[\x00-\x1f<>|?*]' -or
            @($relative.Split('/')) -contains '..' -or
            @($relative.Split('/')) -contains '.' -or
            @($relative.TrimEnd('/').Split('/')) -contains '') {
          throw 'Unsafe path in update ZIP.'
        }
        $parts = @($relative.TrimEnd('/').Split('/'))
        if ($parts.Count -gt 2) { throw 'Nested paths are forbidden in update ZIP.' }
        if ($relative.EndsWith('/')) {
          if ($parts.Count -ne 1) { throw 'Nested directories are forbidden in update ZIP.' }
          continue
        }
        if ($parts[-1] -cnotin $managedNames -and $parts[-1] -ne 'README.md') {
          throw 'Update ZIP includes a non-application file.'
        }
        $totalSize += $entry.Length
        if ($entry.Length -gt 1000000000 -or $totalSize -gt 1500000000) {
          throw 'Update ZIP expanded size limit exceeded.'
        }
      }
    } finally { $archive.Dispose() }
    New-Item -ItemType Directory -Path $tempRoot -ErrorAction Stop | Out-Null
    Expand-Archive -LiteralPath $Package -DestinationPath $tempRoot -ErrorAction Stop
    $exes = @(Get-ChildItem -LiteralPath $tempRoot -Filter DesktopDeck.exe -File -Recurse)
    if ($exes.Count -ne 1) { throw 'The package must contain exactly one DesktopDeck.exe.' }
    $payloadDir = Split-Path -Parent $exes[0].FullName
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

if ($Relaunch) {
  $exe = Join-Path $InstallDir 'DesktopDeck.exe'
  if (Test-Path -LiteralPath $exe -PathType Leaf) { Start-Process -FilePath $exe }
}
