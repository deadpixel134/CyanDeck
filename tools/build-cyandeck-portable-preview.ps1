param(
  [ValidateSet('win-x64', 'win-arm64')]
  [string]$Runtime = 'win-x64',
  [string]$OutputDirectory = (Join-Path $PSScriptRoot "../preview-portable/$Runtime")
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [IO.Path]::GetFullPath($OutputDirectory)

function Assert-OutsideProtectedPath([string]$Candidate, [string]$Protected) {
  if ([string]::IsNullOrWhiteSpace($Protected)) { return }
  $prefix = [IO.Path]::GetFullPath($Protected).TrimEnd('\', '/')
  $candidatePath = $Candidate.TrimEnd('\', '/')
  if ([string]::Equals($candidatePath, $prefix, [StringComparison]::OrdinalIgnoreCase) -or
      $candidatePath.StartsWith($prefix + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Portable preview output cannot use a source, install, release or user-data directory: $Candidate"
  }
}

# No profile migration occurs here: neither installer nor signed update ZIP.
if ([string]::Equals($out.TrimEnd('\', '/'), $root.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Portable preview cannot overwrite the repository root.'
}
foreach ($protected in @(
    (Join-Path $root '.git'), (Join-Path $root 'src'), (Join-Path $root 'dist'),
    (Join-Path $root 'publish'), (Join-Path $root 'json'),
    (Join-Path $env:APPDATA 'DesktopDeck'), (Join-Path $env:APPDATA 'CyanDeck'),
    (Join-Path $env:LOCALAPPDATA 'DesktopDeck'), (Join-Path $env:LOCALAPPDATA 'CyanDeck'),
    (Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck'),
    (Join-Path $env:LOCALAPPDATA 'Programs/CyanDeck'))) {
  Assert-OutsideProtectedPath $out $protected
}
if (Test-Path -LiteralPath $out) {
  throw 'Portable preview output already exists; select a fresh directory instead of overwriting files.'
}

# Refuse junction/symlink output ancestors to protect unrelated user data.
$cursor = $out
while (-not [string]::IsNullOrWhiteSpace($cursor)) {
  if (Test-Path -LiteralPath $cursor) {
    $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "Portable preview refused redirected output path: $cursor"
    }
  }
  $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
  if (-not $parent -or $parent -eq $cursor) { break }
  $cursor = $parent
}

# The preview is intentionally OAuth-unprovisioned. Refuse local secrets.
if (Test-Path -LiteralPath (Join-Path $root 'json/client_secret.json')) {
  throw 'Portable preview refuses a checkout containing OAuth JSON. Use a clean source tree without credentials.'
}
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/DesktopDeck/DesktopDeck.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$') {
  throw 'Invalid DesktopDeck version; refusing portable preview.'
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-portable-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $work 'bundle'
$launcher = Join-Path $work 'launcher'
$legacy = Join-Path $work 'legacy'
try {
  New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
  & (Join-Path $PSScriptRoot 'build-cyandeck-launcher-preview.ps1') -Runtime $Runtime -OutputDirectory $launcher
  & dotnet publish (Join-Path $root 'src/DesktopDeck/DesktopDeck.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $legacy
  if ($LASTEXITCODE -ne 0) { throw 'Portable DesktopDeck companion publish failed.' }
  $legacyExe = Join-Path $legacy 'DesktopDeck.exe'
  if (-not (Test-Path -LiteralPath $legacyExe -PathType Leaf)) {
    throw 'Portable companion DesktopDeck.exe missing.'
  }
  # Publish may emit PDBs or auxiliary files. Construct a clean three-file
  # bundle instead of deleting output files or relaxing manifest validation.
  $launcherExe = Join-Path $launcher 'CyanDeck.exe'
  if (-not (Test-Path -LiteralPath $launcherExe -PathType Leaf)) {
    throw 'Portable launcher CyanDeck.exe missing.'
  }
  New-Item -ItemType Directory -Path $bundle -ErrorAction Stop | Out-Null
  Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $bundle 'CyanDeck.exe') -ErrorAction Stop
  Copy-Item -LiteralPath $legacyExe -Destination (Join-Path $bundle 'DesktopDeck.exe') -ErrorAction Stop
  $notice = @(
    'UNSIGNED CYANDECK PORTABLE COMPATIBILITY PREVIEW - NOT FOR PRODUCTION'
    "Source version: $version; runtime: $Runtime"
    'Contains BOTH CyanDeck.exe (compatibility launcher) and DesktopDeck.exe (legacy application).'
    'Run CyanDeck.exe ONLY in a separate Windows TEST ACCOUNT with sample desktop files and sample calendars.'
    'CAUTION: Portable DOES NOT MEAN isolated data. The companion still reads/writes %APPDATA%\DesktopDeck and %LOCALAPPDATA%\DesktopDeck.'
    'Do not run in your normal account; do not overwrite installed DesktopDeck.exe or copy these EXEs to its install directory.'
    'This is NOT an installer, signed update, migration, rollback package or public release.'
    'Google OAuth is intentionally UNPROVISIONED in this preview (no client JSON embedded).'
    'User data, original icon, install path, update identity and signed release workflow remain unchanged.'
  ) -join [Environment]::NewLine
  [IO.File]::WriteAllText((Join-Path $bundle 'PREVIEW-README.txt'),
    $notice + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

  & (Join-Path $PSScriptRoot 'test_cyandeck_portable_preview.ps1') -BundleDirectory $bundle
  if (Test-Path -LiteralPath $out) {
    throw 'Portable preview output appeared during build; refusing overwrite.'
  }
  New-Item -ItemType Directory -Path $out -ErrorAction Stop | Out-Null
  foreach ($name in @('CyanDeck.exe', 'DesktopDeck.exe', 'PREVIEW-README.txt')) {
    Copy-Item -LiteralPath (Join-Path $bundle $name) -Destination (Join-Path $out $name) -ErrorAction Stop
  }
  & (Join-Path $PSScriptRoot 'test_cyandeck_portable_preview.ps1') -BundleDirectory $out
  Write-Host "Unsigned portable preview prepared: $out"
  Write-Host 'Separate Windows TEST ACCOUNT required; does not activate CyanDeck profile.'
}
finally {
  if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop
  }
}
