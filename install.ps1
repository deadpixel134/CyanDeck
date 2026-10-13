param(
  [switch]$StartWithWindows,
  [switch]$CreateDesktopShortcut
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "CyanDeck.exe"
if (-not (Test-Path $source)) { throw "CyanDeck.exe was not found next to install.ps1." }

$installDir = Join-Path $env:LOCALAPPDATA "Programs\CyanDeck"
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcut = Join-Path $startMenuDir "CyanDeck.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) "CyanDeck.lnk"

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
Assert-NoReparseAncestors (Join-Path $installDir 'CyanDeck.exe')
foreach ($name in @('cyandeck-google-client-id.txt','CyanDeck.update-public-key.pem',
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
foreach ($name in @('cyandeck-google-client-id.txt','CyanDeck.update-public-key.pem')) {
  $optionalSource = Join-Path $PSScriptRoot $name
  if (Test-Path -LiteralPath $optionalSource) { Assert-NoReparseAncestors $optionalSource }
}
Assert-NoReparseAncestors $shortcut
if ($CreateDesktopShortcut) { Assert-NoReparseAncestors $desktopShortcut }

# Do not overwrite a user's unrelated shortcut with the same filename.
# Validate before copying any application files so failure leaves an existing
# installation untouched.
function Assert-ShortcutBelongsToCyanDeck([string]$Path, [string]$ExpectedExe) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
  $shell = New-Object -ComObject WScript.Shell
  $existing = $shell.CreateShortcut($Path)
  if ([string]::IsNullOrWhiteSpace($existing.TargetPath) -or
      -not [string]::Equals([IO.Path]::GetFullPath($existing.TargetPath),
        [IO.Path]::GetFullPath($ExpectedExe), [StringComparison]::OrdinalIgnoreCase)) {
    throw "CyanDeck installer refused to overwrite an unrelated shortcut: $Path"
  }
}
$expectedExe = Join-Path $installDir 'CyanDeck.exe'
Assert-ShortcutBelongsToCyanDeck $shortcut $expectedExe
if ($CreateDesktopShortcut) { Assert-ShortcutBelongsToCyanDeck $desktopShortcut $expectedExe }

# Standalone CyanDeck installation, without legacy-profile migration.
$cyanInstallMutex = [Threading.Mutex]::new($false, 'Local\CyanDeck.SingleInstance')
$ownsCyanInstallMutex = $false
try {
  try { $ownsCyanInstallMutex = $cyanInstallMutex.WaitOne(0) }
  catch [Threading.AbandonedMutexException] {
    $ownsCyanInstallMutex = $true
    throw 'A previous CyanDeck instance stopped unexpectedly. Inspect its data before installing.'
  }
  if (-not $ownsCyanInstallMutex) {
    throw 'CyanDeck is running or another installer is active. Close it before installing.'
  }
# Do not terminate the process while layout/calendar writes may be in flight.
if (@(Get-Process -Name CyanDeck -ErrorAction SilentlyContinue).Count -gt 0) {
  throw 'CyanDeck is running. Please exit from the tray menu and retry the installer.'
}
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Assert-NoReparseAncestors $installDir
Copy-Item $source (Join-Path $installDir "CyanDeck.exe") -Force
# A distributor-supplied public OAuth client ID accompanies the EXE.
$oauthId = Join-Path $PSScriptRoot "cyandeck-google-client-id.txt"
if (Test-Path $oauthId) { Copy-Item $oauthId (Join-Path $installDir "cyandeck-google-client-id.txt") -Force }
# The public verification key must accompany signed releases. Never install
# the private signing key; the updater refuses unsigned manifests.
$updatePublicKey = Join-Path $PSScriptRoot 'CyanDeck.update-public-key.pem'
if (Test-Path -LiteralPath $updatePublicKey -PathType Leaf) {
  Copy-Item -LiteralPath $updatePublicKey -Destination (Join-Path $installDir 'CyanDeck.update-public-key.pem') -Force
}
# Google Desktop OAuth credentials are already embedded inside the EXE.
# A separate JSON must not be required on the target user's PC.
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") (Join-Path $installDir "uninstall.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "apply-update.ps1") (Join-Path $installDir "apply-update.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "recover-desktop-icons.ps1") (Join-Path $installDir "recover-desktop-icons.ps1") -Force

$wsh = New-Object -ComObject WScript.Shell
$link = $wsh.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $installDir "CyanDeck.exe"
$link.WorkingDirectory = $installDir
$link.Description = "CyanDeck desktop organizer"
$link.Save()

if ($CreateDesktopShortcut) {
  $link = $wsh.CreateShortcut($desktopShortcut)
  $link.TargetPath = Join-Path $installDir "CyanDeck.exe"
  $link.WorkingDirectory = $installDir
  $link.Description = "CyanDeck desktop organizer"
  $link.Save()
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($StartWithWindows) {
  New-Item -Path $runKey -Force | Out-Null
  New-ItemProperty -Path $runKey -Name "CyanDeck" -Value ('"' + (Join-Path $installDir "CyanDeck.exe") + '"') -PropertyType String -Force | Out-Null
}

Write-Host "Installed CyanDeck to: $installDir"
Write-Host "Start Menu shortcut: $shortcut"

} finally {
  if ($ownsCyanInstallMutex) { $cyanInstallMutex.ReleaseMutex() }
  $cyanInstallMutex.Dispose()
}
