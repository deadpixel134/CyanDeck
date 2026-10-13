param(
  [Parameter(Mandatory=$true)][string]$NativeExecutable,
  [ValidateSet('win-x64','win-arm64')][string]$Runtime
)
$ErrorActionPreference='Stop'
$oldLocal=$env:LOCALAPPDATA
$oldApp=$env:APPDATA
$root=Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-stage-fixture-'+[Guid]::NewGuid().ToString('N'))
try {
  $env:LOCALAPPDATA=Join-Path $root 'local'
  $env:APPDATA=Join-Path $root 'roaming'
  $legacy=Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck'
  $native=Join-Path $env:LOCALAPPDATA 'Programs/CyanDeck'
  New-Item -ItemType Directory -Path $legacy -Force | Out-Null
  New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
  $legacyExe=Join-Path $legacy 'DesktopDeck.exe'
  Set-Content -LiteralPath $legacyExe -Value 'original old EXE fixture' -Encoding ASCII
  $legacyHash=(Get-FileHash -LiteralPath $legacyExe -Algorithm SHA256).Hash
  $key=[Security.Cryptography.RSA]::Create(2048)
  $priv=Join-Path $root 'private.pem'
  try {
    [IO.File]::WriteAllText($priv,$key.ExportPkcs8PrivateKeyPem(),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $legacy 'DesktopDeck.update-public-key.pem'),
      $key.ExportSubjectPublicKeyInfoPem(),[Text.UTF8Encoding]::new($false))
  } finally { $key.Dispose() }
  $bundle=Join-Path $root 'signed-bundle'
  & (Join-Path $PSScriptRoot 'package-cyandeck-native-candidate.ps1') -Runtime $Runtime -NativeExecutable $NativeExecutable -SigningKeyPath $priv -OutputDirectory $bundle
  $manifest=Join-Path $bundle 'candidate-manifest.json'
  $tool=Join-Path $PSScriptRoot 'stage-cyandeck-native-handoff.ps1'
  & $tool -CandidateManifest $manifest
  if (Test-Path -LiteralPath $native) { throw 'Dry run modified the native installation path.' }
  # Self-signed candidate cannot replace the separately pinned legacy trust key.
  $otherKey=[Security.Cryptography.RSA]::Create(2048)
  try {
    [IO.File]::WriteAllText((Join-Path $legacy 'DesktopDeck.update-public-key.pem'),
      $otherKey.ExportSubjectPublicKeyInfoPem(),[Text.UTF8Encoding]::new($false))
  } finally { $otherKey.Dispose() }
  $rejected=$false
  try { & $tool -CandidateManifest $manifest -Apply | Out-Null } catch { $rejected=$true }
  if (-not $rejected -or (Test-Path -LiteralPath $native)) {
    throw 'Staging accepted a candidate signed by a different key.'
  }
  $pinned=[Security.Cryptography.RSA]::Create()
  try {
    $pinned.ImportFromPem((Get-Content -LiteralPath $priv -Raw))
    [IO.File]::WriteAllText((Join-Path $legacy 'DesktopDeck.update-public-key.pem'),
      $pinned.ExportSubjectPublicKeyInfoPem(),[Text.UTF8Encoding]::new($false))
  } finally { $pinned.Dispose() }
  & $tool -CandidateManifest $manifest -Apply
  if (-not (Test-Path -LiteralPath (Join-Path $native 'CyanDeck.exe') -PathType Leaf)) {
    throw 'Verified native executable was not staged.'
  }
  if ((Get-FileHash -LiteralPath $legacyExe -Algorithm SHA256).Hash -cne $legacyHash) {
    throw 'Staging changed an original DesktopDeck executable.'
  }
  $rejected=$false
  try { & $tool -CandidateManifest $manifest -Apply | Out-Null } catch { $rejected=$true }
  if (-not $rejected) { throw 'Repeated staging overwrote the existing candidate.' }
  Write-Host 'Signed native staging dry-run, pinned trust rejection, atomic copy and no-overwrite checks passed.'
}
finally {
  $env:LOCALAPPDATA=$oldLocal
  $env:APPDATA=$oldApp
  if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
