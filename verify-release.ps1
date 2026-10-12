param(
  [Parameter(Mandatory=$true)][string]$ManifestPath,
  [string]$PublicKeyPath = ""
)
$ErrorActionPreference = "Stop"
$manifestPathResolved = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $manifestPathResolved -Raw | ConvertFrom-Json
$packagePath = if ([IO.Path]::IsPathRooted([string]$manifest.packageUrl)) { [string]$manifest.packageUrl } else { Join-Path (Split-Path -Parent $manifestPathResolved) ([string]$manifest.packageUrl) }
if (-not (Test-Path -LiteralPath $packagePath)) { throw "Package not found: $packagePath" }
$actual = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = ([string]$manifest.sha256).ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch. expected=$expected actual=$actual" }

function Escape-ManifestValue([string]$Value) {
  if ($null -eq $Value) { return "" }
  return $Value.Replace('\', '\\').Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", '\n')
}
function Get-CanonicalManifestPayload($Manifest) {
  $v = Escape-ManifestValue (([string]$Manifest.version).Trim())
  $runtimeValue = Escape-ManifestValue (([string]$Manifest.runtime).Trim())
  $packageValue = Escape-ManifestValue (([string]$Manifest.packageUrl).Trim())
  $hashValue = Escape-ManifestValue (([string]$Manifest.sha256).Trim().ToLowerInvariant())
  $notesUrlValue = Escape-ManifestValue (([string]$Manifest.releaseNotesUrl).Trim())
  $notesValue = Escape-ManifestValue ([string]$Manifest.notes)
  $lines = @(
    "DesktopDeck.UpdateManifest.v1",
    "version=$v",
    "schema=$($Manifest.schema)",
    "runtime=$runtimeValue",
    "packageUrl=$packageValue",
    "sha256=$hashValue",
    "releaseNotesUrl=$notesUrlValue",
    "notes=$notesValue"
  )
  return [string]::Join("`n", $lines)
}

if (-not [string]::IsNullOrWhiteSpace([string]$manifest.signature)) {
  if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) {
    $candidate = Join-Path (Split-Path -Parent $manifestPathResolved) "DesktopDeck.update-public-key.pem"
    if (Test-Path $candidate) { $PublicKeyPath = $candidate } else { throw "Manifest is signed; provide -PublicKeyPath." }
  }
  $rsa = [System.Security.Cryptography.RSA]::Create()
  try {
    $rsa.ImportFromPem((Get-Content -LiteralPath $PublicKeyPath -Raw))
    $fingerprint = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace([string]$manifest.keyId) -and ([string]$manifest.keyId).ToLowerInvariant() -ne $fingerprint) { throw "Signing key ID mismatch." }
    $payload = [Text.Encoding]::UTF8.GetBytes((Get-CanonicalManifestPayload $manifest))
    $signature = [Convert]::FromBase64String([string]$manifest.signature)
    if (-not $rsa.VerifyData($payload, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)) { throw "Manifest signature verification failed." }
    Write-Host "Manifest signature verified: $fingerprint"
  } finally { $rsa.Dispose() }
} else {
  throw "Unsigned update manifest is not permitted in a release."
}
Write-Host "Release verification PASS: $packagePath"
