param([Parameter(Mandatory=$true)][string]$NativeExecutable,
  [ValidateSet('win-x64','win-arm64')][string]$Runtime)
$ErrorActionPreference='Stop'
$work=Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-sign-test-'+[Guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
  $private=Join-Path $work 'ephemeral-private.pem'
  $candidate=Join-Path $work 'bundle'
  $key=[Security.Cryptography.RSA]::Create(2048)
  try { [IO.File]::WriteAllText($private,$key.ExportPkcs8PrivateKeyPem(),[Text.UTF8Encoding]::new($false)) }
  finally { $key.Dispose() }
  & (Join-Path $PSScriptRoot 'package-cyandeck-native-candidate.ps1') -Runtime $Runtime -NativeExecutable $NativeExecutable -SigningKeyPath $private -OutputDirectory $candidate
  & (Join-Path $PSScriptRoot 'verify-cyandeck-native-candidate.ps1') -ManifestPath (Join-Path $candidate 'candidate-manifest.json')
  $manifest=Get-Content -LiteralPath (Join-Path $candidate 'candidate-manifest.json') -Raw | ConvertFrom-Json
  $zip=Join-Path $candidate $manifest.archiveName
  $bytes=[IO.File]::ReadAllBytes($zip)
  $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
  [IO.File]::WriteAllBytes($zip,$bytes)
  $rejected=$false
  try { & (Join-Path $PSScriptRoot 'verify-cyandeck-native-candidate.ps1') -ManifestPath (Join-Path $candidate 'candidate-manifest.json') | Out-Null }
  catch { $rejected=$true }
  if (-not $rejected) { throw 'Tampered signed candidate was accepted.' }
  Write-Host 'CyanDeck signed native candidate generation and tamper rejection passed.'
}
finally { if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force } }
