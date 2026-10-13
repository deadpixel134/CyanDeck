param(
  [Parameter(Mandatory=$true)][string]$CandidateManifest,
  [switch]$Apply
)
# Stage ONLY the signed native binary. Does not launch, update, switch profile,
# replace old DesktopDeck, edit shortcuts or modify Windows startup entries.
$ErrorActionPreference='Stop'
if (-not $env:LOCALAPPDATA -or -not $env:APPDATA) {
  throw 'Windows profile environment unavailable.'
}
$legacy=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck'))
$programs=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$target=[IO.Path]::GetFullPath((Join-Path $programs 'CyanDeck'))
$manifest=[IO.Path]::GetFullPath($CandidateManifest)
function Assert-SafeAncestor([string]$Path) {
  $cursor=[IO.Path]::GetFullPath($Path)
  while ($cursor) {
    if (Test-Path -LiteralPath $cursor) {
      $entry=Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
      if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Staging refused a redirected Windows profile, install path or candidate.'
      }
    }
    $parent=[IO.Path]::GetDirectoryName($cursor.TrimEnd('\','/'))
    if (-not $parent -or $parent -eq $cursor) { break }
    $cursor=$parent
  }
}
foreach ($path in @($legacy,$programs,$target,$manifest)) {
  Assert-SafeAncestor $path
}
if (-not (Test-Path -LiteralPath $legacy -PathType Container) -or
    -not (Test-Path -LiteralPath (Join-Path $legacy 'DesktopDeck.exe') -PathType Leaf)) {
  throw 'A managed legacy DesktopDeck installation is required before a native candidate can be staged.'
}
$pinnedKey=Join-Path $legacy 'DesktopDeck.update-public-key.pem'
Assert-SafeAncestor $pinnedKey
if (-not (Test-Path -LiteralPath $pinnedKey -PathType Leaf)) {
  throw 'No trusted installed update key; refusing candidate-key trust on first use.'
}
if (Test-Path -LiteralPath $target) {
  throw 'A CyanDeck installation/staging destination already exists; no merge or overwrite permitted.'
}
# The signature and archive hash must be verified against the ALREADY
# INSTALLED trusted DesktopDeck public key, not the key shipped with the ZIP.
& (Join-Path $PSScriptRoot 'verify-cyandeck-native-candidate.ps1') -ManifestPath $manifest -PublicKeyPath $pinnedKey
if (-not $Apply) {
  Write-Host 'DRY RUN: candidate signature and pinned trust key verified; no changes made.'
  Write-Host 'Native CyanDeck still uses the original DesktopDeck user data and updater.'
  return
}
$mut1=[Threading.Mutex]::new($false,'Local\DesktopDeck.SingleInstance')
$mut2=[Threading.Mutex]::new($false,'Local\CyanDeck.LegacyProfileWriter.v1')
$owns1=$false
$owns2=$false
$work=$null
try {
  try { $owns1=$mut1.WaitOne(0) }
  catch [Threading.AbandonedMutexException] {
    $owns1=$true
    throw 'Previous DesktopDeck writer abandoned its mutex. Inspect its data first.'
  }
  if (-not $owns1) { throw 'DesktopDeck is running. Close it before staging CyanDeck.' }
  try { $owns2=$mut2.WaitOne(0) }
  catch [Threading.AbandonedMutexException] {
    $owns2=$true
    throw 'Previous CyanDeck writer abandoned its mutex. Inspect its data first.'
  }
  if (-not $owns2) { throw 'Another writer is using the shared profile.' }
  if (@(Get-Process -Name DesktopDeck,CyanDeck -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Exit all DesktopDeck and CyanDeck processes before staging.'
  }
  Assert-SafeAncestor $target
  if (Test-Path -LiteralPath $target) { throw 'Destination appeared while staging; refusing overwrite.' }
  # Revalidate under both writer locks to reject a source replacement race.
  & (Join-Path $PSScriptRoot 'verify-cyandeck-native-candidate.ps1') -ManifestPath $manifest -PublicKeyPath $pinnedKey
  $record=Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
  $archivePath=Join-Path ([IO.Path]::GetDirectoryName($manifest)) ([string]$record.archiveName)
  if (-not (Test-Path -LiteralPath $programs)) {
    New-Item -ItemType Directory -Path $programs -Force -ErrorAction Stop | Out-Null
  }
  Assert-SafeAncestor $programs
  $work=Join-Path $programs ('.cyandeck-stage-'+[Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
  $zip=[IO.Compression.ZipFile]::OpenRead($archivePath)
  try {
    foreach ($name in @('CyanDeck.exe','CANDIDATE-NOTICE.txt')) {
      $item=$zip.GetEntry($name)
      if ($null -eq $item) { throw 'Verified archive entry vanished.' }
      $inputStream=$item.Open()
      $dest=Join-Path $work $name
      try {
        $output=[IO.FileStream]::new($dest,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,
          [IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try { $inputStream.CopyTo($output); $output.Flush($true) }
        finally { $output.Dispose() }
      } finally { $inputStream.Dispose() }
    }
  } finally { $zip.Dispose() }
  # Recheck the signature and source archive immediately before publishing.
  & (Join-Path $PSScriptRoot 'verify-cyandeck-native-candidate.ps1') -ManifestPath $manifest -PublicKeyPath $pinnedKey
  if (Test-Path -LiteralPath $target) { throw 'Destination changed before publish.' }
  [IO.Directory]::Move($work,$target)
  $work=$null
  Write-Host 'SIGNED NATIVE CANDIDATE STAGED ONLY (not installed or activated).'
  Write-Host 'No legacy file, data root, Windows shortcut or updater was changed.'
}
finally {
  if ($work -and (Test-Path -LiteralPath $work -PathType Container)) {
    Assert-SafeAncestor $work
    foreach ($name in @('CyanDeck.exe','CANDIDATE-NOTICE.txt')) {
      $file=Join-Path $work $name
      if (Test-Path -LiteralPath $file -PathType Leaf) {
        Assert-SafeAncestor $file
        Remove-Item -LiteralPath $file -Force
      }
    }
    # Leave unexpected files alone rather than ever deleting user content.
    if (@(Get-ChildItem -LiteralPath $work -Force).Count -eq 0) {
      Remove-Item -LiteralPath $work -Force
    }
  }
  if ($owns2) { $mut2.ReleaseMutex() }
  $mut2.Dispose()
  if ($owns1) { $mut1.ReleaseMutex() }
  $mut1.Dispose()
}
