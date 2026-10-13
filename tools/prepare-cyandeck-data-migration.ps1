param(
  [switch]$Apply,
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
if (Test-Path -LiteralPath $destination) {
  throw "Destination already exists; refusing to merge or overwrite: $destination"
}
$files = @(Get-ChildItem -LiteralPath $legacy -File -Recurse -Force)
foreach ($file in $files) {
  if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Refusing linked file: $($file.FullName)"
  }
}
$directories = @(Get-ChildItem -LiteralPath $legacy -Directory -Recurse -Force)
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
  $currentFiles = @(Get-ChildItem -LiteralPath $legacy -File -Recurse -Force)
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
  if (Test-Path -LiteralPath $destination) { throw 'Destination appeared during migration; refusing overwrite.' }
  Move-Item -LiteralPath $staging -Destination $destination -ErrorAction Stop
  Write-Host 'Verified CyanDeck data copy prepared. Original DesktopDeck data remains untouched.'
  Write-Host 'The application still reads legacy paths; this command does NOT activate the new location.'
} finally {
  if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
  }
}
