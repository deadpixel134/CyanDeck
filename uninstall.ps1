param(
  [switch]$PurgeData,
  [switch]$ConfirmPurgeData
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DesktopDeck'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DesktopDeck.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DesktopDeck.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
# Reject junctions for the managed install directory to avoid deleting files
# in unrelated directories that have the same filenames.
if (Test-Path -LiteralPath $installDir -PathType Container) {
  if (((Get-Item -LiteralPath $installDir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Uninstallation refused: installation directory is a junction or symbolic link.'
  }
}

# Graceful exit is a prerequisite, not a forced Stop-Process. This avoids
# corrupting a pending layout or calendar save during uninstallation.
if (@(Get-Process DesktopDeck -ErrorAction SilentlyContinue).Count -gt 0) {
  throw 'CyanDeck is running. Exit from the tray menu before uninstalling.'
}

if ($PurgeData -and -not $ConfirmPurgeData) {
  throw 'Deleting user data requires both -PurgeData and -ConfirmPurgeData. A backup will be retained.'
}
if ($PurgeData) {
  $backupRoot = Join-Path $env:LOCALAPPDATA 'DesktopDeck-uninstall-backups'
  $backupDir = Join-Path $backupRoot ([Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
  foreach ($folder in @((Join-Path $env:APPDATA 'DesktopDeck'), (Join-Path $env:LOCALAPPDATA 'DesktopDeck'))) {
    if (Test-Path -LiteralPath $folder -PathType Container) {
      # Inspect subdirectories without -Recurse: Windows PowerShell 5.1 may
      # follow directory junctions before they can be rejected.
      $pending = [System.Collections.Generic.Stack[string]]::new()
      $pending.Push($folder)
      while ($pending.Count -gt 0) {
        $candidate = $pending.Pop()
        $candidateInfo = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
        if (($candidateInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
          throw 'Refusing to purge a data directory containing junctions or symbolic links.'
        }
        if ($candidateInfo.PSIsContainer) {
          foreach ($child in (Get-ChildItem -LiteralPath $candidate -Force -ErrorAction Stop)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
              throw 'Refusing to purge a data directory containing junctions or symbolic links.'
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
          }
        }
      }
      $label = if ($folder.StartsWith($env:APPDATA, [StringComparison]::OrdinalIgnoreCase)) { 'Roaming' } else { 'Local' }
      Copy-Item -LiteralPath $folder -Destination (Join-Path $backupDir $label) -Recurse -ErrorAction Stop
    }
  }
  Write-Host "User data safety backup: $backupDir (may contain Google tokens; keep it private)"
}

# Remove only links pointing to the installed application. Do not delete
# user-created shortcuts with a coincidentally matching filename.
$wsh = New-Object -ComObject WScript.Shell
foreach ($shortcut in @($startMenuShortcut, $desktopShortcut)) {
  if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
    try {
      $link = $wsh.CreateShortcut($shortcut)
      $expected = [IO.Path]::GetFullPath((Join-Path $installDir 'DesktopDeck.exe'))
      if ([string]::Equals([IO.Path]::GetFullPath($link.TargetPath), $expected, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $shortcut -Force -ErrorAction Stop
      }
    } catch { Write-Warning "Shortcut was not removed: $shortcut ($($_.Exception.Message))" }
  }
}

$runValue = (Get-ItemProperty -Path $runKey -Name 'DesktopDeck' -ErrorAction SilentlyContinue).DesktopDeck
if (-not [string]::IsNullOrWhiteSpace($runValue) -and
    $runValue.Trim('"') -ieq (Join-Path $installDir 'DesktopDeck.exe')) {
  Remove-ItemProperty -Path $runKey -Name 'DesktopDeck' -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath 'HKCU:\Software\Classes\DesktopBackground\Shell\DesktopDeck.RecoverGroups' -Recurse -Force -ErrorAction SilentlyContinue

# Remove known application files only; never recursively remove the entire
# install directory (portable installs may contain unrelated user files).
$owned = @('DesktopDeck.exe','apply-update.ps1','recover-desktop-icons.ps1','install.ps1','uninstall.ps1',
           'verify-release.ps1','desktopdeck-google-client-id.txt',
           'DesktopDeck.update-public-key.pem')
foreach ($name in $owned) {
  $path = Join-Path $installDir $name
  if (Test-Path -LiteralPath $path -PathType Leaf) {
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      Write-Warning "Refusing to remove reparse-point file: $path"
      continue
    }
    Remove-Item -LiteralPath $path -Force -ErrorAction Stop
  }
}
# uninstall.ps1 can be the currently executing script, so leave it for manual
# cleanup if Windows still has a handle open. An empty dir may be removed.
if ((Test-Path -LiteralPath $installDir -PathType Container) -and
    @(Get-ChildItem -LiteralPath $installDir -Force).Count -eq 0) {
  Remove-Item -LiteralPath $installDir -ErrorAction SilentlyContinue
}

if ($PurgeData) {
  foreach ($folder in @((Join-Path $env:APPDATA 'DesktopDeck'), (Join-Path $env:LOCALAPPDATA 'DesktopDeck'))) {
    if (Test-Path -LiteralPath $folder -PathType Container) {
      Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction Stop
    }
  }
}
Write-Host 'CyanDeck was uninstalled (unrelated files preserved).'
if (-not $PurgeData) { Write-Host 'Your settings were preserved in %APPDATA%\DesktopDeck.' }
