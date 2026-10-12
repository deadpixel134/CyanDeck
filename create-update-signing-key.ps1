param(
  [Parameter(Mandatory=$true)][string]$DestinationDirectory
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run with PowerShell 7+: pwsh -File create-update-signing-key.ps1 -DestinationDirectory ...' }
$target = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DestinationDirectory))
if (-not (Test-Path -LiteralPath $target -PathType Container)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
$private = Join-Path $target 'DesktopDeck.update-private-key.pem'
$public = Join-Path $target 'DesktopDeck.update-public-key.pem'
if ((Test-Path -LiteralPath $private) -or (Test-Path -LiteralPath $public)) { throw 'A signing key already exists. Never overwrite it: installed copies trust the old public key.' }
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
  [IO.File]::WriteAllText($private, $rsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText($public, $rsa.ExportSubjectPublicKeyInfoPem(), [Text.UTF8Encoding]::new($false))
  $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
  Write-Host "PRIVATE KEY (do not commit/share): $private"
  Write-Host "PUBLIC KEY (ship with initial release): $public"
  Write-Host "Signing key fingerprint: $digest"
  Write-Host 'GitHub Actions secret: DESKTOPDECK_UPDATE_SIGNING_KEY_PEM = complete text of the PRIVATE PEM.'
} finally { $rsa.Dispose() }
