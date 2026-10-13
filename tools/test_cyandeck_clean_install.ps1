# Disposable Windows-profile integration test for clean CyanDeck installation.
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-clean-install-'+[Guid]::NewGuid().ToString('N'))
$oldAppData=$env:APPDATA
$oldLocal=$env:LOCALAPPDATA
try {
  $env:APPDATA=Join-Path $root 'roaming'
  $env:LOCALAPPDATA=Join-Path $root 'local'
  $source=Join-Path $root 'source'
  $legacyRoam=Join-Path $env:APPDATA 'DesktopDeck'
  $legacyInstall=Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck'
  $cyanRoam=Join-Path $env:APPDATA 'CyanDeck'
  $cyanInstall=Join-Path $env:LOCALAPPDATA 'Programs/CyanDeck'
  foreach ($folder in @($source,$legacyRoam,$legacyInstall,$cyanRoam)) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
  }
  Set-Content -LiteralPath (Join-Path $legacyRoam 'layout.json') -Value 'old-product-layout' -Encoding ASCII
  Set-Content -LiteralPath (Join-Path $legacyInstall 'DesktopDeck.exe') -Value 'old-product-exe' -Encoding ASCII
  Set-Content -LiteralPath (Join-Path $cyanRoam 'calendar-events.json') -Value '[]' -Encoding ASCII
  $legacyHash=(Get-FileHash -LiteralPath (Join-Path $legacyRoam 'layout.json') -Algorithm SHA256).Hash
  Set-Content -LiteralPath (Join-Path $source 'CyanDeck.exe') -Value 'new-product-fixture' -Encoding ASCII
  foreach ($name in @('install.ps1','uninstall.ps1','apply-update.ps1','recover-desktop-icons.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\$name") -Destination (Join-Path $source $name)
  }
  & (Join-Path $source 'install.ps1')
  if (-not (Test-Path -LiteralPath (Join-Path $cyanInstall 'CyanDeck.exe') -PathType Leaf)) {
    throw 'Clean CyanDeck installation did not create its own executable.'
  }
  if (-not (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\CyanDeck.lnk'))) {
    throw 'Clean CyanDeck installation did not create its Start Menu entry.'
  }
  if ((Get-FileHash -LiteralPath (Join-Path $legacyRoam 'layout.json') -Algorithm SHA256).Hash -ne $legacyHash) {
    throw 'Clean install modified old product data.'
  }
  & (Join-Path $source 'uninstall.ps1')
  if (Test-Path -LiteralPath (Join-Path $cyanInstall 'CyanDeck.exe')) {
    throw 'Clean uninstall left the managed CyanDeck executable.'
  }
  if (-not (Test-Path -LiteralPath (Join-Path $cyanRoam 'calendar-events.json'))) {
    throw 'Uninstall without purge removed local CyanDeck calendar data.'
  }
  if (-not (Test-Path -LiteralPath (Join-Path $legacyInstall 'DesktopDeck.exe')) -or
      (Get-FileHash -LiteralPath (Join-Path $legacyRoam 'layout.json') -Algorithm SHA256).Hash -ne $legacyHash) {
    throw 'Uninstall touched the independent old product.'
  }
  Write-Host 'Clean CyanDeck install/uninstall preserved both user data roots and unrelated product.'
}
finally {
  $env:APPDATA=$oldAppData
  $env:LOCALAPPDATA=$oldLocal
  if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
