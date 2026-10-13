param(
  [Parameter(Mandatory = $true)]
  [string]$BundleDirectory
)

$ErrorActionPreference = 'Stop'
$bundle = [IO.Path]::GetFullPath($BundleDirectory)
if (-not (Test-Path -LiteralPath $bundle -PathType Container)) {
  throw 'Missing portable preview directory.'
}
$required = @('CyanDeck.exe', 'DesktopDeck.exe', 'PREVIEW-README.txt')
$entries = @(Get-ChildItem -LiteralPath $bundle -Force -ErrorAction Stop)
if ($entries.Count -ne $required.Count) {
  throw 'Portable preview must contain exactly two executables and PREVIEW-README.txt.'
}
foreach ($entry in $entries) {
  if ($entry.PSIsContainer -or $entry.Name -cnotin $required -or
      ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Unsafe or unexpected portable preview entry: $($entry.Name)"
  }
}
foreach ($name in $required) {
  $file = Join-Path $bundle $name
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    throw "Portable preview missing file: $name"
  }
}
[xml]$legacyProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/DesktopDeck/DesktopDeck.csproj') -Raw
$version = [string]$legacyProject.Project.PropertyGroup.Version
foreach ($name in @('CyanDeck.exe', 'DesktopDeck.exe')) {
  $path = Join-Path $bundle $name
  if ((Get-Item -LiteralPath $path).Length -lt 10000) {
    throw "Portable executable unexpectedly small: $name"
  }
  $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
  if ($info.FileVersion -ne $version -and
      -not $info.FileVersion.StartsWith($version + '.', [StringComparison]::Ordinal)) {
    throw "Portable preview executable version mismatch: $name"
  }
}
$notice = Get-Content -LiteralPath (Join-Path $bundle 'PREVIEW-README.txt') -Raw
foreach ($phrase in @('UNSIGNED', 'NOT FOR PRODUCTION', 'TEST ACCOUNT',
                      '%APPDATA%\DesktopDeck', 'CyanDeck.exe', 'DesktopDeck.exe',
                      'NOT an installer', 'UNPROVISIONED')) {
  if (-not $notice.Contains($phrase)) {
    throw "Portable preview notice is missing a required safety statement: $phrase"
  }
}
Write-Host 'CyanDeck portable preview manifest, versions and safety notices validated.'
