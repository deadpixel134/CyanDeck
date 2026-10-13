param(
  [switch]$StartWithWindows,
  [switch]$CreateDesktopShortcut
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "DesktopDeck.exe"
if (-not (Test-Path $source)) { throw "DesktopDeck.exe was not found next to install.ps1." }

$installDir = Join-Path $env:LOCALAPPDATA "Programs\DesktopDeck"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcut = Join-Path $startMenuDir "DesktopDeck.lnk"
$cyanShortcut = Join-Path $startMenuDir "CyanDeck.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) "DesktopDeck.lnk"

# A fixed per-user directory is not sufficient protection: a parent component,
# the installation directory, or an existing destination file can be a junction/
# symbolic link pointing to unrelated user data. Reject the whole path chain.
function Assert-NoReparseAncestors([string]$Target) {
  $cursor = [IO.Path]::GetFullPath($Target)
  while (-not [string]::IsNullOrWhiteSpace($cursor)) {
    if (Test-Path -LiteralPath $cursor) {
      $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "CyanDeck installer refused reparse-point path: $cursor"
      }
    }
    $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
    if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cursor) { break }
    $cursor = $parent
  }
}

Assert-NoReparseAncestors $installDir
Assert-NoReparseAncestors (Join-Path $installDir 'DesktopDeck.exe')
foreach ($name in @('desktopdeck-google-client-id.txt','DesktopDeck.update-public-key.pem',
                   'uninstall.ps1','apply-update.ps1','recover-desktop-icons.ps1')) {
  Assert-NoReparseAncestors (Join-Path $installDir $name)
}
# Source must be a regular EXE rather than a link into another location.
Assert-NoReparseAncestors $source
# Validate all required helper sources BEFORE overwriting any installed file.
# A half-copied update can otherwise leave an existing installation unusable.
foreach ($name in @('uninstall.ps1','apply-update.ps1','recover-desktop-icons.ps1')) {
  $helperSource = Join-Path $PSScriptRoot $name
  Assert-NoReparseAncestors $helperSource
  if (-not (Test-Path -LiteralPath $helperSource -PathType Leaf)) {
    throw "Required CyanDeck installer helper is missing: $name"
  }
}
foreach ($name in @('desktopdeck-google-client-id.txt','DesktopDeck.update-public-key.pem')) {
  $optionalSource = Join-Path $PSScriptRoot $name
  if (Test-Path -LiteralPath $optionalSource) { Assert-NoReparseAncestors $optionalSource }
}
Assert-NoReparseAncestors $shortcut
Assert-NoReparseAncestors $cyanShortcut
if ($CreateDesktopShortcut) { Assert-NoReparseAncestors $desktopShortcut }

# Do not overwrite a user's unrelated shortcut with the same filename.
# Validate before copying any application files so failure leaves an existing
# installation untouched.
function Assert-ShortcutBelongsToDesktopDeck([string]$Path, [string]$ExpectedExe) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
  $shell = New-Object -ComObject WScript.Shell
  $existing = $shell.CreateShortcut($Path)
  if ([string]::IsNullOrWhiteSpace($existing.TargetPath) -or
      -not [string]::Equals([IO.Path]::GetFullPath($existing.TargetPath),
        [IO.Path]::GetFullPath($ExpectedExe), [StringComparison]::OrdinalIgnoreCase)) {
    throw "CyanDeck installer refused to overwrite an unrelated shortcut: $Path"
  }
}
$expectedExe = Join-Path $installDir 'DesktopDeck.exe'
Assert-ShortcutBelongsToDesktopDeck $shortcut $expectedExe
Assert-ShortcutBelongsToDesktopDeck $cyanShortcut $expectedExe
if ($CreateDesktopShortcut) { Assert-ShortcutBelongsToDesktopDeck $desktopShortcut $expectedExe }

# This installer remains a legacy-profile application. It must not put an
# old DesktopDeck.exe back on disk after a future CyanDeck profile cutover.
# Presence of the cutover fence is a hard stop even when JSON is malformed.
function Assert-LegacyWriterFenceAbsent {
  $fencePath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'DesktopDeck\migration\legacy-writer-fenced.json'))
  $cursor = $fencePath
  while ($cursor) {
    $attributes = $null
    try { $attributes = [IO.File]::GetAttributes($cursor) }
    catch [IO.FileNotFoundException] { }
    catch [IO.DirectoryNotFoundException] { }
    if ($null -ne $attributes) {
      if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Unsafe CyanDeck legacy profile fence path: $cursor"
      }
      if ([string]::Equals($cursor, $fencePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'CyanDeck profile cutover fence is present. Legacy DesktopDeck reinstall/downgrade is blocked.'
      }
    }
    $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
    if (-not $parent -or $parent -eq $cursor) { break }
    $cursor = $parent
  }
}

# Acquire in the same order as the application and migration tool. The old
# mutex excludes released DesktopDeck builds; the new lease spans future EXE
# renames. Hold both until all installed files and shortcuts are committed.
$legacyInstallMutex = [Threading.Mutex]::new($false, 'Local\DesktopDeck.SingleInstance')
$writerInstallMutex = [Threading.Mutex]::new($false, 'Local\CyanDeck.LegacyProfileWriter.v1')
$ownsLegacyInstallMutex = $false
$ownsWriterInstallMutex = $false
try {
  try { $ownsLegacyInstallMutex = $legacyInstallMutex.WaitOne(0) }
  catch [Threading.AbandonedMutexException] {
    $ownsLegacyInstallMutex = $true
    throw 'A previous DesktopDeck instance stopped unexpectedly. Inspect its data before installing.'
  }
  if (-not $ownsLegacyInstallMutex) {
    throw 'DesktopDeck is running or another installer is active. Close it before installing.'
  }
  try { $ownsWriterInstallMutex = $writerInstallMutex.WaitOne(0) }
  catch [Threading.AbandonedMutexException] {
    $ownsWriterInstallMutex = $true
    throw 'A legacy-profile writer stopped unexpectedly. Inspect its data before installing.'
  }
  if (-not $ownsWriterInstallMutex) {
    throw 'Another CyanDeck writer or data migration holds the legacy profile. Installation cancelled.'
  }
  Assert-LegacyWriterFenceAbsent
# Do not terminate the process while layout/calendar writes may be in flight.
if (@(Get-Process -Name DesktopDeck,CyanDeck -ErrorAction SilentlyContinue).Count -gt 0) {
  throw 'CyanDeck is running. Please exit from the tray menu and retry the installer.'
}
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Assert-NoReparseAncestors $installDir
Copy-Item $source (Join-Path $installDir "DesktopDeck.exe") -Force
# A distributor-supplied public OAuth client ID accompanies the EXE.
$oauthId = Join-Path $PSScriptRoot "desktopdeck-google-client-id.txt"
if (Test-Path $oauthId) { Copy-Item $oauthId (Join-Path $installDir "desktopdeck-google-client-id.txt") -Force }
# The public verification key must accompany signed releases. Never install
# the private signing key; the updater refuses unsigned manifests.
$updatePublicKey = Join-Path $PSScriptRoot 'DesktopDeck.update-public-key.pem'
if (Test-Path -LiteralPath $updatePublicKey -PathType Leaf) {
  Copy-Item -LiteralPath $updatePublicKey -Destination (Join-Path $installDir 'DesktopDeck.update-public-key.pem') -Force
}
# Google Desktop OAuth credentials are already embedded inside the EXE.
# A separate JSON must not be required on the target user's PC.
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") (Join-Path $installDir "uninstall.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "apply-update.ps1") (Join-Path $installDir "apply-update.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "recover-desktop-icons.ps1") (Join-Path $installDir "recover-desktop-icons.ps1") -Force

$wsh = New-Object -ComObject WScript.Shell
$link = $wsh.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $installDir "DesktopDeck.exe"
$link.WorkingDirectory = $installDir
$link.Description = "CyanDeck desktop organizer"
$link.Save()

# Preserve the legacy shortcut for existing pins and upgrades. Add a separate
# CyanDeck Start Menu entry only after validating ownership and reparse safety.
$cyanLink = $wsh.CreateShortcut($cyanShortcut)
$cyanLink.TargetPath = Join-Path $installDir "DesktopDeck.exe"
$cyanLink.WorkingDirectory = $installDir
$cyanLink.Description = "CyanDeck desktop organizer"
$cyanLink.Save()

if ($CreateDesktopShortcut) {
  $link = $wsh.CreateShortcut($desktopShortcut)
  $link.TargetPath = Join-Path $installDir "DesktopDeck.exe"
  $link.WorkingDirectory = $installDir
  $link.Description = "CyanDeck desktop organizer"
  $link.Save()
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($StartWithWindows) {
  New-Item -Path $runKey -Force | Out-Null
  New-ItemProperty -Path $runKey -Name "DesktopDeck" -Value ('"' + (Join-Path $installDir "DesktopDeck.exe") + '"') -PropertyType String -Force | Out-Null
}

Write-Host "Installed CyanDeck to: $installDir"
Write-Host "Start Menu shortcut: $cyanShortcut (legacy: $shortcut)"

} finally {
  if ($ownsWriterInstallMutex) { $writerInstallMutex.ReleaseMutex() }
  $writerInstallMutex.Dispose()
  if ($ownsLegacyInstallMutex) { $legacyInstallMutex.ReleaseMutex() }
  $legacyInstallMutex.Dispose()
}
