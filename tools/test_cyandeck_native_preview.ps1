param([Parameter(Mandatory=$true)][string]$BundleDirectory)
$ErrorActionPreference='Stop'
$bundle = [IO.Path]::GetFullPath($BundleDirectory)
$expected = @('CyanDeck.exe','NATIVE-PREVIEW-README.txt')
if (-not (Test-Path -LiteralPath $bundle -PathType Container)) { throw 'Native preview missing.' }
$items = @(Get-ChildItem -LiteralPath $bundle -Force)
if ($items.Count -ne $expected.Count) { throw 'Native preview has unexpected files.' }
foreach ($item in $items) {
  if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
      $item.Name -cnotin $expected) { throw 'Unsafe native preview entry.' }
}
$exe = Join-Path $bundle 'CyanDeck.exe'
$notice = Join-Path $bundle 'NATIVE-PREVIEW-README.txt'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
    -not (Test-Path -LiteralPath $notice -PathType Leaf)) { throw 'Native preview incomplete.' }
[xml]$proj = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/DesktopDeck/DesktopDeck.csproj') -Raw
$version = [string]$proj.Project.PropertyGroup.Version
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ((Get-Item -LiteralPath $exe).Length -lt 10000 -or
    ($info.FileVersion -ne $version -and
      -not $info.FileVersion.StartsWith($version+'.',[StringComparison]::Ordinal))) {
  throw 'Native WPF executable version mismatch.'
}
$text = Get-Content -LiteralPath $notice -Raw
foreach ($phrase in @('FULL WPF APPLICATION','NOT the compatibility launcher',
  'TEST ACCOUNT','%APPDATA%\DesktopDeck','UNPROVISIONED','DISABLED')) {
  if (-not $text.Contains($phrase)) { throw 'Native preview notice incomplete.' }
}
Write-Host 'Native CyanDeck WPF binary and preview safety statements validated.'
