# Validate the actual signed CyanDeck release archive by installing and
# uninstalling it inside a disposable Windows user profile.
# This does NOT launch CyanDeck.exe or touch the interactive desktop.
param(
  [Parameter(Mandatory=$true)]
  [ValidateSet('win-x64', 'win-arm64')]
  [string]$Runtime,
  [switch]$RequireOAuth
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
[xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/CyanDeck/CyanDeck.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$dist = Join-Path $repositoryRoot 'dist'
$zip = Join-Path $dist "CyanDeck-$version-$Runtime.zip"
$manifest = Join-Path $dist "CyanDeck-$version-$Runtime.update.json"
if (-not (Test-Path -LiteralPath $zip -PathType Leaf) -or
    -not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
  throw 'A signed CyanDeck release ZIP and manifest must exist before the package smoke test.'
}

# The production verifier checks the package SHA-256 and RSA-PSS signature
# using the public key shipped with this particular build.
& (Join-Path $repositoryRoot 'verify-release.ps1') -ManifestPath $manifest
if (-not $?) { throw 'Package signature check failed.' }
$metadata = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ([string]$metadata.version -cne $version -or
    [string]$metadata.runtime -cne $Runtime -or
    [string]$metadata.packageUrl -cne [IO.Path]::GetFileName($zip) -or
    [string]$metadata.signatureAlgorithm -cne 'RSA-PSS-SHA256') {
  throw 'Signed manifest metadata does not match the actual CyanDeck build.'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$allowed = @(
  'CyanDeck.exe', 'README.md', 'install.ps1', 'uninstall.ps1',
  'apply-update.ps1', 'recover-desktop-icons.ps1', 'verify-release.ps1',
  'CyanDeck.update-public-key.pem', 'cyandeck-google-client-id.txt'
)
$required = @(
  'CyanDeck.exe', 'README.md', 'install.ps1', 'uninstall.ps1',
  'apply-update.ps1', 'recover-desktop-icons.ps1', 'verify-release.ps1',
  'CyanDeck.update-public-key.pem'
)
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
  $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
  $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($name in $names) {
    # Release builds are deliberately flat, so extraction cannot reach
    # arbitrary paths or duplicate files through case-insensitive names.
    if ($name -notin $allowed -or -not $seen.Add($name)) {
      throw "Unexpected or duplicate release package entry: $name"
    }
  }
  foreach ($name in $required) {
    if ($name -notin $names) { throw "Required release package file missing: $name" }
  }
  if ($RequireOAuth -and 'cyandeck-google-client-id.txt' -notin $names) {
    throw 'Google-provisioned release must include its public OAuth client ID.'
  }
  if ($names -match 'client_secret|oauth-client\.json|\.p12|\.pfx|private-key') {
    throw 'A private OAuth JSON or private signing key must not be distributed.'
  }
  $binary = $archive.GetEntry('CyanDeck.exe')
  if ($null -eq $binary -or $binary.Length -lt 10MB) {
    throw 'CyanDeck.exe does not resemble a self-contained Windows application.'
  }
  $reader = $binary.Open()
  try {
    $magic = New-Object byte[] 2
    if ($reader.Read($magic, 0, 2) -ne 2 -or
        $magic[0] -ne 0x4d -or $magic[1] -ne 0x5a) {
      throw 'CyanDeck.exe does not have a Windows PE header.'
    }
  } finally { $reader.Dispose() }
} finally { $archive.Dispose() }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-real-package-'+[Guid]::NewGuid().ToString('N'))
$oldRoaming = $env:APPDATA
$oldLocal = $env:LOCALAPPDATA
try {
  New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
  $payload = Join-Path $sandbox 'payload'
  Expand-Archive -LiteralPath $zip -DestinationPath $payload
  $sourceExe = Join-Path $payload 'CyanDeck.exe'
  $sourceHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
  $env:APPDATA = Join-Path $sandbox 'roaming'
  $env:LOCALAPPDATA = Join-Path $sandbox 'local'

  $calendarData = Join-Path $env:APPDATA 'CyanDeck/calendar-events.json'
  $oldData = Join-Path $env:APPDATA 'DesktopDeck/layout.json'
  $oldExe = Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck/DesktopDeck.exe'
  $installed = Join-Path $env:LOCALAPPDATA 'Programs/CyanDeck'
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $calendarData) | Out-Null
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $oldData) | Out-Null
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $oldExe) | Out-Null
  Set-Content -LiteralPath $calendarData -Value 'independent-local-calendar' -Encoding ASCII
  Set-Content -LiteralPath $oldData -Value 'unrelated-legacy-profile' -Encoding ASCII
  Set-Content -LiteralPath $oldExe -Value 'unrelated-legacy-executable' -Encoding ASCII
  $oldDataHash = (Get-FileHash -LiteralPath $oldData -Algorithm SHA256).Hash
  $oldExeHash = (Get-FileHash -LiteralPath $oldExe -Algorithm SHA256).Hash

  & (Join-Path $payload 'install.ps1')
  if (-not $?) { throw 'Actual signed release installer failed.' }
  $installedExe = Join-Path $installed 'CyanDeck.exe'
  if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf) -or
      (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -cne $sourceHash) {
    throw 'Installed CyanDeck binary is different from the release ZIP binary.'
  }
  foreach ($name in @('apply-update.ps1', 'uninstall.ps1', 'recover-desktop-icons.ps1', 'CyanDeck.update-public-key.pem')) {
    if (-not (Test-Path -LiteralPath (Join-Path $installed $name) -PathType Leaf)) {
      throw "Required installed helper/public key missing: $name"
    }
  }
  # Every release ZIP must install a verification PEM matching its
  # signed manifest (the executable also pins the same public key).
  $installedRsa = [Security.Cryptography.RSA]::Create()
  try {
    $installedRsa.ImportFromPem((Get-Content -LiteralPath (Join-Path $installed 'CyanDeck.update-public-key.pem') -Raw))
    $installedKeyId = [Convert]::ToHexString(
      [Security.Cryptography.SHA256]::HashData($installedRsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    if ($installedKeyId -cne [string]$metadata.keyId) {
      throw 'Installed public key does not match the signed release manifest.'
    }
  } finally { $installedRsa.Dispose() }
  if ($RequireOAuth) {
    $id = (Get-Content -LiteralPath (Join-Path $installed 'cyandeck-google-client-id.txt') -Raw).Trim()
    if ($id -notmatch '^[0-9]+-[A-Za-z0-9_-]+\.apps\.googleusercontent\.com$') {
      throw 'Installed Google OAuth public client ID is malformed.'
    }
  }
  $link = Join-Path $env:APPDATA 'Microsoft/Windows/Start Menu/Programs/CyanDeck.lnk'
  if (-not (Test-Path -LiteralPath $link -PathType Leaf)) {
    throw 'Actual release installer did not create its Start Menu shortcut.'
  }
  $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
  if (-not [string]::Equals([IO.Path]::GetFullPath($shortcut.TargetPath),
      [IO.Path]::GetFullPath($installedExe), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installed Start Menu shortcut points outside the CyanDeck installation.'
  }
  & (Join-Path $installed 'uninstall.ps1')
  if (-not $?) { throw 'Actual release uninstaller failed.' }
  if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
    throw 'Uninstall left the application executable installed.'
  }
  if (-not (Test-Path -LiteralPath $calendarData -PathType Leaf) -or
      (Get-Content -LiteralPath $calendarData -Raw).Trim() -cne 'independent-local-calendar') {
    throw 'Uninstall changed local calendar data.'
  }
  if ((Get-FileHash -LiteralPath $oldData -Algorithm SHA256).Hash -cne $oldDataHash -or
      (Get-FileHash -LiteralPath $oldExe -Algorithm SHA256).Hash -cne $oldExeHash) {
    throw 'Release installer or uninstaller changed an unrelated product.'
  }
  if (Test-Path -LiteralPath $link -PathType Leaf) {
    throw 'Uninstall left the owned Start Menu shortcut.'
  }
  Write-Host "PASS: Real signed release install/uninstall smoke ($Runtime, provisioned=$RequireOAuth)."
}
finally {
  $env:APPDATA = $oldRoaming
  $env:LOCALAPPDATA = $oldLocal
  if (Test-Path -LiteralPath $sandbox -PathType Container) {
    Remove-Item -LiteralPath $sandbox -Recurse -Force
  }
}
