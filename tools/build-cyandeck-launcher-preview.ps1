param(
  [ValidateSet('win-x64', 'win-arm64')]
  [string]$Runtime = 'win-x64',
  [string]$OutputDirectory = (Join-Path $PSScriptRoot "../preview/$Runtime")
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $root 'src/CyanDeck.Launcher/CyanDeck.Launcher.csproj'

# Preview metadata follows the installed legacy application version. There is
# no new active profile, updater protocol, installer, or executable cutover.
[xml]$legacyProject = Get-Content -LiteralPath (Join-Path $root 'src/DesktopDeck/DesktopDeck.csproj') -Raw
$legacyVersion = [string]$legacyProject.Project.PropertyGroup.Version
if ($legacyVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$') {
  throw 'DesktopDeck version is invalid; refusing CyanDeck preview build.'
}

# Never write preview content into the installed or signed release directory.
foreach ($protected in @((Join-Path $root "publish/$Runtime"),
                         (Join-Path $root 'dist'),
                         (Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck'))) {
  $path = [IO.Path]::GetFullPath($protected).TrimEnd('\','/')
  if ([string]::Equals($out.TrimEnd('\','/'), $path, [StringComparison]::OrdinalIgnoreCase) -or
      $out.StartsWith($path + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CyanDeck preview may not overwrite an installed, published or signed legacy executable.'
  }
}
& dotnet publish $project -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:Version=$legacyVersion" -o $out
if ($LASTEXITCODE -ne 0) { throw "CyanDeck preview build failed (exit $LASTEXITCODE)." }
$exe = Join-Path $out 'CyanDeck.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
  throw 'Preview build did not produce CyanDeck.exe.'
}

$notice = @(
  'CyanDeck.exe - UNSIGNED compatibility preview'
  "Source version: $legacyVersion"
  'Place only beside a trusted DesktopDeck.exe. This preview does not activate CyanDeck data.'
  'This is not a signed update, installer, or replacement of DesktopDeck.exe.'
) -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $out 'PREVIEW-README.txt'),
  $notice + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "Preview only: $exe"
Write-Host 'Place beside a compatible DesktopDeck.exe in a separate portable test directory.'
Write-Host 'Signed releases, installed application and persistent data remain unchanged.'
