param(
  [ValidateSet('win-x64', 'win-arm64')]
  [string]$Runtime = 'win-x64',
  [string]$OutputDirectory = (Join-Path $PSScriptRoot "../preview/$Runtime")
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $root 'src/CyanDeck.Launcher/CyanDeck.Launcher.csproj'
# Keep preview output away from installed or signed legacy release content.
foreach ($protected in @((Join-Path $root "publish/$Runtime"),
                         (Join-Path $root 'dist'),
                         (Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck'))) {
  $path = [IO.Path]::GetFullPath($protected).TrimEnd('\','/')
  if ([string]::Equals($out.TrimEnd('\','/'), $path, [StringComparison]::OrdinalIgnoreCase) -or
      $out.StartsWith($path + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CyanDeck preview may not overwrite an installed, published or signed legacy executable.'
  }
}
& dotnet publish $project -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "CyanDeck preview build failed (exit $LASTEXITCODE)." }
$exe = Join-Path $out 'CyanDeck.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
  throw 'Preview build did not produce CyanDeck.exe.'
}
Write-Host "Preview only: $exe"
Write-Host 'Place beside a compatible DesktopDeck.exe in a separate portable test directory.'
Write-Host 'Signed releases, installed application and persistent data remain unchanged.'
