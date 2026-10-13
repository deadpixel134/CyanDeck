param(
  [switch]$Apply,
  [switch]$VerifyPrepared,
  [switch]$AbortPreparation,
  [string]$DestinationRoot = (Join-Path $env:APPDATA 'CyanDeck')
)
# Opt-in, non-destructive preparation only. Runtime still uses DesktopDeck.
$ErrorActionPreference = 'Stop'
$legacy = Join-Path $env:APPDATA 'DesktopDeck'
$destination = [IO.Path]::GetFullPath($DestinationRoot)
$legacy = [IO.Path]::GetFullPath($legacy)
function Assert-SafePath([string]$Path) {
  $cursor = [IO.Path]::GetFullPath($Path)
  while ($cursor) {
    if (Test-Path -LiteralPath $cursor) {
      $item = Get-Item -LiteralPath $cursor -Force
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing reparse-point path: $cursor"
      }
    }
    $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
    if (-not $parent -or $parent -eq $cursor) { break }
    $cursor = $parent
  }
}
# Walk one directory at a time: reject junctions and symlinks before
# descending, rather than checking them after a recursive enumeration.
function Get-SafeTree([string]$Root) {
  $pending = [Collections.Generic.Stack[string]]::new()
  $pending.Push($Root)
  while ($pending.Count -gt 0) {
    $current = $pending.Pop()
    Assert-SafePath $current
    foreach ($entry in (Get-ChildItem -LiteralPath $current -Force)) {
      if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing linked migration item: $($entry.FullName)"
      }
      $entry
      if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
    }
  }
}
# Mirror the non-activating C# journal format and transition rules. Only the
# canonical CyanDeck destination is eligible for a shared journal record.
function Assert-JournalTimestamp($Value) {
  if (-not $Value) { throw 'Migration journal timestamp is missing.' }
  $parsed = [DateTimeOffset]::MinValue
  if (-not [DateTimeOffset]::TryParse([string]$Value,
      [Globalization.CultureInfo]::InvariantCulture,
      [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed) -or
      $parsed -eq [DateTimeOffset]::MinValue -or
      $parsed -gt [DateTimeOffset]::UtcNow.AddMinutes(10)) {
    throw 'Migration journal timestamp is invalid.'
  }
}
function Write-CutoverJournal([string]$Phase) {
  $canonical = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'CyanDeck'))
  if (-not [string]::Equals($destination, $canonical, [StringComparison]::OrdinalIgnoreCase)) { return }
  $journalDir = Join-Path (Join-Path $env:LOCALAPPDATA 'DesktopDeck') 'migration'
  Assert-SafePath $journalDir
  New-Item -ItemType Directory -Path $journalDir -Force | Out-Null
  $journal = Join-Path $journalDir 'cyandeck-cutover-state.json'
  Assert-SafePath $journal
  Assert-SafePath ($journal + '.lock')
  Assert-SafePath ($journal + '.backup')
  $gate = [IO.FileStream]::new($journal + '.lock', [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
  try {
    $previous = $null
    if (Test-Path -LiteralPath $journal -PathType Leaf) {
      if ((Get-Item -LiteralPath $journal -Force).Length -gt 16384) { throw 'Migration journal exceeds 16 KiB.' }
      if ((Get-Item -LiteralPath $journal -Force).Length -gt 16384) { throw 'Migration journal exceeds 16 KiB.' }
  $previous = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
      Assert-JournalTimestamp $previous.RecordedAtUtc
      if ($previous.Version -ne 1 -or $previous.Phase -notin @('prepared','verified','aborted') -or
          -not $previous.RecordedAtUtc -or
          -not [string]::Equals($previous.LegacyRoot, $legacy, [StringComparison]::OrdinalIgnoreCase) -or
          -not [string]::Equals($previous.CandidateRoot, $canonical, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unrecognized existing migration journal.'
      }
    }
    if ($Phase -eq 'prepared' -and $previous -and $previous.Phase -ne 'aborted') {
      throw 'Existing migration journal must be aborted before preparing again.'
    }
    if ($Phase -eq 'verified' -and (-not $previous -or $previous.Phase -notin @('prepared','verified'))) {
      throw 'Verification requires a prepared migration journal.'
    }
    if ($Phase -eq 'aborted' -and (-not $previous -or $previous.Phase -eq 'aborted')) { return }
    $record = [ordered]@{
      Version = 1; Phase = $Phase; RecordedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
      LegacyRoot = $legacy; CandidateRoot = $canonical
    }
    $temp = $journal + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
      $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Compress))
      $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
      try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
      finally { $stream.Dispose() }
      # File.Move(source, destination, overwrite) is unavailable on the
      # .NET Framework runtime used by Windows PowerShell 5.1.
      # File.Replace atomically updates an existing journal on Windows;
      # File.Move publishes the first journal without overwriting it.
      if (Test-Path -LiteralPath $journal -PathType Leaf) {
        [IO.File]::Replace($temp, $journal, ($journal + '.backup'))
      } else {
        [IO.File]::Move($temp, $journal)
      }
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
  } finally { $gate.Dispose() }
}
# Reject a stale or incompatible journal BEFORE creating the candidate.
# This avoids publishing a copy that cannot be recorded as prepared.
function Assert-CanPrepareJournal {
  $canonical = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'CyanDeck'))
  if (-not [string]::Equals($destination, $canonical, [StringComparison]::OrdinalIgnoreCase)) { return }
  $journal = Join-Path (Join-Path (Join-Path $env:LOCALAPPDATA 'DesktopDeck') 'migration') 'cyandeck-cutover-state.json'
  Assert-SafePath $journal
  if (-not (Test-Path -LiteralPath $journal)) { return }
  $previous = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
  Assert-JournalTimestamp $previous.RecordedAtUtc
  if ($previous.Version -ne 1 -or $previous.Phase -notin @('prepared','verified','aborted') -or
      -not [string]::Equals($previous.LegacyRoot, $legacy, [StringComparison]::OrdinalIgnoreCase) -or
      -not [string]::Equals($previous.CandidateRoot, $canonical, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unrecognized migration journal; refusing to prepare.'
  }
  if ($previous.Phase -ne 'aborted') {
    throw 'Existing migration journal must be aborted before preparing again.'
  }
}
# Share the application's actual single-instance mutex. Holding ownership
# prevents an app instance from starting during the copy/verification window.
# Process-name checks alone cannot close the startup race.
$migrationMutex = [Threading.Mutex]::new($false, 'Local\DesktopDeck.SingleInstance')
$ownsMigrationMutex = $false
try {
  try {
    $ownsMigrationMutex = $migrationMutex.WaitOne(0)
  } catch [Threading.AbandonedMutexException] {
    # WaitOne grants ownership even when it reports abandonment. Release it
    # in the outer finally, but refuse to stage data after an unclean exit.
    $ownsMigrationMutex = $true
    throw 'DesktopDeck single-instance mutex was abandoned; retry after inspecting application state.'
  }
  if (-not $ownsMigrationMutex) {
    throw 'DesktopDeck is running or another migration holds its single-instance mutex.'
  }
if ($AbortPreparation) {
  if ($Apply -or $VerifyPrepared) { throw 'Use -AbortPreparation by itself.' }
  $canonical = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'CyanDeck'))
  if (-not [string]::Equals($destination, $canonical, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'AbortPreparation requires the canonical CyanDeck destination.'
  }
  Assert-SafePath $legacy
  Assert-SafePath $destination
  if (@(Get-Process -Name DesktopDeck,CyanDeck -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Exit all DesktopDeck and CyanDeck processes before aborting migration preparation.'
  }
  Write-CutoverJournal 'aborted'
  Write-Host 'Migration preparation marked aborted. Neither profile was modified.'
  return
}
Assert-SafePath $legacy
Assert-SafePath $destination
if ([string]::Equals($legacy, $destination, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Source and destination must be different.'
}
if ($destination.StartsWith($legacy.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $legacy.StartsWith($destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Source and destination must not be nested.'
}
if (-not (Test-Path -LiteralPath $legacy -PathType Container)) {
  Write-Host 'No legacy DesktopDeck data directory found; nothing to prepare.'
  return
}
if (@(Get-Process -Name DesktopDeck,CyanDeck -ErrorAction SilentlyContinue).Count -gt 0) {
  throw 'Exit all DesktopDeck and CyanDeck processes before preparing a data migration.'
}
if ($VerifyPrepared) {
  if ($Apply) { throw 'Use -VerifyPrepared or -Apply, not both.' }
  if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
    throw 'No prepared CyanDeck directory to verify.'
  }
  Assert-SafePath $destination
  $original = @(Get-SafeTree $legacy | Where-Object { -not $_.PSIsContainer })
  $copied = @(Get-SafeTree $destination | Where-Object { -not $_.PSIsContainer })
  $originalDirs = @(Get-SafeTree $legacy | Where-Object { $_.PSIsContainer })
  $copiedDirs = @(Get-SafeTree $destination | Where-Object { $_.PSIsContainer })
  foreach ($item in @($original) + @($copied) + @($originalDirs) + @($copiedDirs)) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "Refusing linked migration item: $($item.FullName)"
    }
  }
  if ($original.Count -ne $copied.Count -or $originalDirs.Count -ne $copiedDirs.Count) {
    throw 'Prepared copy has a different file or directory count.'
  }
  foreach ($file in $original) {
    $relative = $file.FullName.Substring($legacy.Length).TrimStart('\')
    $target = Join-Path $destination $relative
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
      throw "Prepared copy differs from original: $relative"
    }
  }
  foreach ($dir in $originalDirs) {
    $relative = $dir.FullName.Substring($legacy.Length).TrimStart('\')
    if (-not (Test-Path -LiteralPath (Join-Path $destination $relative) -PathType Container)) {
      throw "Prepared copy is missing directory: $relative"
    }
  }
  Write-CutoverJournal 'verified'
  Write-Host 'Prepared copy matches the current legacy data. No files were changed.'
  Write-Host 'Verification does not activate the CyanDeck data root.'
  return
}
if (Test-Path -LiteralPath $destination) {
  throw "Destination already exists; refusing to merge or overwrite: $destination"
}
Assert-CanPrepareJournal
$files = @(Get-SafeTree $legacy | Where-Object { -not $_.PSIsContainer })
foreach ($file in $files) {
  if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Refusing linked file: $($file.FullName)"
  }
}
$directories = @(Get-SafeTree $legacy | Where-Object { $_.PSIsContainer })
foreach ($dir in $directories) {
  if (($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Refusing linked directory: $($dir.FullName)"
  }
}
# Check every recovery candidate, not just the primary file. Do not
# promote corrupt backups or a future schema into a new data root.
foreach ($name in @('layout.json', 'layout.json.bak', 'layout.json.tmp',
                     'calendar-events.json', 'calendar-events.json.bak', 'calendar-events.json.tmp')) {
  $candidate = Join-Path $legacy $name
  if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
  try {
    $raw = Get-Content -LiteralPath $candidate -Raw -Encoding UTF8 -ErrorAction Stop
    $parsed = ConvertFrom-Json -InputObject $raw -NoEnumerate -ErrorAction Stop
    if ($name.StartsWith('layout.json', [StringComparison]::OrdinalIgnoreCase)) {
      if ($null -eq $parsed -or $parsed -isnot [pscustomobject]) { throw 'Layout must be an object.' }
      if ($null -ne $parsed.Version) {
        $version = 0
        if (-not [int]::TryParse([string]$parsed.Version, [ref]$version) -or $version -lt 0 -or $version -gt 35) {
          throw 'Unsupported layout schema version.'
        }
      }
    } else {
      if ($parsed -isnot [array]) { throw 'Calendar must be an array.' }
      foreach ($event in $parsed) {
        if ($null -eq $event -or $event -isnot [pscustomobject]) { throw 'Calendar event must be an object.' }
      }
    }
  } catch { throw "Migration preflight failed for $name; original data unchanged: $_" }
}
# Ensure all files remained unchanged during preflight. The source is not
# a transactional snapshot, so reject changes while staging and before publish.
$sourceManifest = @{}
foreach ($file in $files) {
  $relative = $file.FullName.Substring($legacy.Length).TrimStart('\')
  $sourceManifest[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
Write-Host "Source: $legacy"
Write-Host "Destination: $destination"
Write-Host "Files: $($files.Count)"
if (-not $Apply) {
  Write-Host 'DRY RUN only. Add -Apply to create a verified copy. Runtime paths remain unchanged.'
  return
}
$parent = [IO.Path]::GetDirectoryName($destination)
Assert-SafePath $parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staging = Join-Path $parent ('.cyandeck-migration-' + [Guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $staging | Out-Null
  foreach ($dir in $directories) {
    $relative = $dir.FullName.Substring($legacy.Length).TrimStart('\')
    New-Item -ItemType Directory -Path (Join-Path $staging $relative) -Force | Out-Null
  }
  foreach ($file in $files) {
    $relative = $file.FullName.Substring($legacy.Length).TrimStart('\')
    $target = Join-Path $staging $relative
    $targetDir = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -ErrorAction Stop
    $a = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $b = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($a -cne $sourceManifest[$relative] -or $a -cne $b) {
      throw "Source changed or checksum mismatch during migration: $relative"
    }
  }
  $currentFiles = @(Get-SafeTree $legacy | Where-Object { -not $_.PSIsContainer })
  if ($currentFiles.Count -ne $sourceManifest.Count) {
    throw 'Source file set changed during migration; refusing to publish.'
  }
  foreach ($file in $currentFiles) {
    $relative = $file.FullName.Substring($legacy.Length).TrimStart('\')
    if (-not $sourceManifest.ContainsKey($relative) -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne $sourceManifest[$relative]) {
      throw "Source changed during migration: $relative"
    }
  }
  # Files can remain unchanged while empty directories are added or removed.
  # Verify the full directory set again immediately before publishing.
  $currentDirs = @(Get-SafeTree $legacy | Where-Object { $_.PSIsContainer })
  if ($currentDirs.Count -ne $directories.Count) {
    throw 'Source directory set changed during migration; refusing to publish.'
  }
  $expectedDirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($dir in $directories) {
    [void]$expectedDirs.Add($dir.FullName.Substring($legacy.Length).TrimStart('\'))
  }
  foreach ($dir in $currentDirs) {
    $relative = $dir.FullName.Substring($legacy.Length).TrimStart('\')
    if (-not $expectedDirs.Contains($relative)) {
      throw "Source directory changed during migration: $relative"
    }
  }
  if (@(Get-Process -Name DesktopDeck,CyanDeck -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'DesktopDeck or CyanDeck started during migration; refusing to publish.'
  }
  if (Test-Path -LiteralPath $destination) { throw 'Destination appeared during migration; refusing overwrite.' }
  Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
  Write-CutoverJournal 'prepared'
  Write-Host 'Verified CyanDeck data copy prepared. Original DesktopDeck data remains untouched.'
  Write-Host 'The application still reads legacy paths; this command does NOT activate the new location.'
} finally {
  if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
  }
}

} finally {
  if ($ownsMigrationMutex) { $migrationMutex.ReleaseMutex() }
  $migrationMutex.Dispose()
}
