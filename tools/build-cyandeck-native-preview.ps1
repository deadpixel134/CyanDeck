param(
  [ValidateSet('win-x64','win-arm64')]
  [string]$Runtime = 'win-x64',
  [string]$OutputDirectory = (Join-Path $PSScriptRoot "../preview-native/$Runtime")
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $env:APPDATA -or -not $env:LOCALAPPDATA) { throw 'Missing Windows profile paths.' }
if (Test-Path -LiteralPath $out) { throw 'Native preview output exists; refusing overwrite.' }
if ([string]::Equals($out,$root,[StringComparison]::OrdinalIgnoreCase)) {
  throw 'Native preview cannot overwrite repository root.'
}
foreach ($blocked in @(
    (Join-Path $root 'src'), (Join-Path $root 'dist'),
    (Join-Path $root 'publish'), (Join-Path $root '.git'),
    (Join-Path $root 'json'), (Join-Path $env:APPDATA 'DesktopDeck'),
    (Join-Path $env:APPDATA 'CyanDeck'), (Join-Path $env:LOCALAPPDATA 'DesktopDeck'),
    (Join-Path $env:LOCALAPPDATA 'CyanDeck'),
    (Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck'),
    (Join-Path $env:LOCALAPPDATA 'Programs/CyanDeck'))) {
  $prefix = [IO.Path]::GetFullPath($blocked).TrimEnd('\','/')
  if ([string]::Equals($out,$prefix,[StringComparison]::OrdinalIgnoreCase) -or
      $out.StartsWith($prefix+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native preview cannot target source, release, installation or profile directories.'
  }
}
$cursor = $out
while ($cursor) {
  if (Test-Path -LiteralPath $cursor) {
    $entry = Get-Item -LiteralPath $cursor -Force
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw 'Native preview output path contains a redirected link.'
    }
  }
  $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\','/'))
  if (-not $parent -or $parent -eq $cursor) { break }
  $cursor = $parent
}
if (Test-Path -LiteralPath (Join-Path $root 'json/client_secret.json')) {
  throw 'Unsigned preview refuses embedded OAuth credentials.'
}
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/DesktopDeck/DesktopDeck.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$') {
  throw 'Unable to validate WPF application version.'
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-native-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $temp 'published'
try {
  New-Item -ItemType Directory -Path $temp -ErrorAction Stop | Out-Null
  & dotnet publish (Join-Path $root 'src/DesktopDeck/DesktopDeck.csproj') -c Release -r $Runtime --self-contained true -p:CyanDeckNative=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $stage
  if ($LASTEXITCODE -ne 0) { throw 'Native CyanDeck WPF publish failed.' }
  $exe = Join-Path $stage 'CyanDeck.exe'
  if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Native publish did not generate CyanDeck.exe.'
  }
  $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
  if ($fileVersion -ne $version -and -not $fileVersion.StartsWith($version+'.',[StringComparison]::Ordinal)) {
    throw 'Native WPF executable version mismatch.'
  }
  New-Item -ItemType Directory -Path $out -ErrorAction Stop | Out-Null
  Copy-Item -LiteralPath $exe -Destination (Join-Path $out 'CyanDeck.exe') -ErrorAction Stop
  $message = @(
    'UNSIGNED NATIVE CYANDECK WPF PREVIEW - NOT A PRODUCTION RELEASE'
    "Source version: $version; runtime: $Runtime"
    'CyanDeck.exe is a FULL WPF APPLICATION, NOT the compatibility launcher.'
    'Use ONLY a separate Windows TEST ACCOUNT with disposable desktop files.'
    'CAUTION: still writes LEGACY %APPDATA%\DesktopDeck and %LOCALAPPDATA%\DesktopDeck data.'
    'Another directory in the same Windows account is NOT profile isolation.'
    'Never overwrite installed DesktopDeck.exe or CyanDeck.exe with this preview.'
    'This build is UNPROVISIONED for Google OAuth and is NOT a signed installer or update.'
    'Data-root activation and signed installer cutover remain DISABLED.'
    'Original icon, mutex and activation IPC remain compatible with DesktopDeck.'
  ) -join [Environment]::NewLine
  [IO.File]::WriteAllText((Join-Path $out 'NATIVE-PREVIEW-README.txt'),$message+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
  Write-Host "Built native CyanDeck WPF compatibility preview: $out"
}
finally {
  if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
