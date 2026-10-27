param(
  [ValidateSet("win-x64", "win-arm64")]
  [string]$Runtime = "win-x64",
  [string]$SigningKeyPath = $env:CYANDECK_UPDATE_SIGNING_KEY
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SigningKeyPath)) {
  throw 'Production release packaging requires -SigningKeyPath (RSA PEM). Use build.cmd for local builds without signing.'
}
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Embed the verified signing key's PUBLIC SPKI into the release executable.
# Never embed or distribute the private signing key.
$releasePublicPemPath = Join-Path ([IO.Path]::GetTempPath()) ('CyanDeck-public-' + [Guid]::NewGuid().ToString('N') + '.pem')
$releaseBuildRsa = $null
try {
  $expandedSigningKey = [Environment]::ExpandEnvironmentVariables($SigningKeyPath)
  if (-not (Test-Path -LiteralPath $expandedSigningKey -PathType Leaf)) {
    throw 'Release signing private key was not found.'
  }
  $releaseBuildRsa = [Security.Cryptography.RSA]::Create()
  $releaseBuildRsa.ImportFromPem((Get-Content -LiteralPath $expandedSigningKey -Raw))
  $embeddedKeyFingerprint = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($releaseBuildRsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
  [IO.File]::WriteAllText($releasePublicPemPath,
    $releaseBuildRsa.ExportSubjectPublicKeyInfoPem(), [Text.UTF8Encoding]::new($false))
} finally {
  if ($null -ne $releaseBuildRsa) { $releaseBuildRsa.Dispose() }
}

try {
& (Join-Path $root "build.ps1") -Runtime $Runtime -ReleasePublicKeyPath $releasePublicPemPath

# Keep the signed manifest in lockstep with the actual compiled app and state
# format. Hard-coded values previously advertised schema 34 for schema 35 data.
[xml]$projectXml = Get-Content (Join-Path $root "src\CyanDeck\CyanDeck.csproj") -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$schemaSource = Get-Content (Join-Path $root "src\CyanDeck\Services\StateSchema.cs") -Raw
$schemaMatch = [regex]::Match($schemaSource, 'public\s+const\s+int\s+Current\s*=\s*(\d+)')
if ([string]::IsNullOrWhiteSpace($version) -or -not $schemaMatch.Success) {
  throw "Cannot determine project version/state schema; aborting package"
}
$schema = [int]$schemaMatch.Groups[1].Value
$stage = Join-Path $root "dist\CyanDeck-$version-$Runtime"
$zip = Join-Path $root "dist\CyanDeck-$version-$Runtime.zip"
# Never wipe a staging directory that may contain someone else's files.
# Fail closed rather than including unexpected files in the release ZIP.
$knownStageFiles = @('CyanDeck.exe','cyandeck-google-client-id.txt',
  'install.ps1','uninstall.ps1','apply-update.ps1','recover-desktop-icons.ps1','verify-release.ps1',
  'README.md','CyanDeck.update-public-key.pem')
if (Test-Path -LiteralPath $stage) {
  $unknown = @(Get-ChildItem -LiteralPath $stage -Force | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $knownStageFiles })
  if ($unknown.Count -gt 0) { throw 'Release staging contains unrelated files; move those files elsewhere before packaging.' }
  foreach ($name in $knownStageFiles) {
    $old = Join-Path $stage $name
    if (Test-Path -LiteralPath $old -PathType Leaf) {
      if ((Get-Item -LiteralPath $old -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release staging contains an unsafe link.' }
      Remove-Item -LiteralPath $old -Force
    }
  }
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $root "publish\$Runtime\CyanDeck.exe") $stage
# Direct standalone downloads use exactly the signed ZIP's own executable.
# x64 retains the CyanDeck.exe name for one-time EXE-only bootstrap.
$standaloneName = if ($Runtime -eq 'win-x64') { 'CyanDeck.exe' } else { 'CyanDeck-ARM64.exe' }
Copy-Item -LiteralPath (Join-Path $stage 'CyanDeck.exe') -Destination (Join-Path $root "dist\$standaloneName") -Force
# Desktop OAuth client ID is public application metadata, not a user's token.
$oauthId = Join-Path $root "publish\$Runtime\cyandeck-google-client-id.txt"
if (Test-Path $oauthId) { Copy-Item $oauthId $stage -Force }
# OAuth native-app credentials are embedded in the EXE at developer build time;
# never distribute a separate client JSON in a release package.
Copy-Item (Join-Path $root "install.ps1") $stage
Copy-Item (Join-Path $root "uninstall.ps1") $stage
Copy-Item (Join-Path $root "apply-update.ps1") $stage
Copy-Item (Join-Path $root "recover-desktop-icons.ps1") $stage
Copy-Item (Join-Path $root "verify-release.ps1") $stage
Copy-Item (Join-Path $root "README.md") $stage

$rsa = $null
$keyId = ""
if (-not [string]::IsNullOrWhiteSpace($SigningKeyPath)) {
  $SigningKeyPath = [Environment]::ExpandEnvironmentVariables($SigningKeyPath)
  if (-not (Test-Path -LiteralPath $SigningKeyPath)) { throw "Signing key not found: $SigningKeyPath" }
  $rsa = [System.Security.Cryptography.RSA]::Create()
  $rsa.ImportFromPem((Get-Content -LiteralPath $SigningKeyPath -Raw))
  $publicBytes = $rsa.ExportSubjectPublicKeyInfo()
  $keyId = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($publicBytes)).ToLowerInvariant()
  if ($keyId -cne $embeddedKeyFingerprint) {
    throw 'Signing key changed between compiling the EXE and signing its manifest.'
  }
  [IO.File]::WriteAllText((Join-Path $stage "CyanDeck.update-public-key.pem"), $rsa.ExportSubjectPublicKeyInfoPem(), [Text.UTF8Encoding]::new($false))
  # The manifest lives in dist/, so copy the public key next to it too: the
  # verify-release.ps1 verifier reads it from the manifest directory.
  [IO.File]::WriteAllText((Join-Path $root "dist\CyanDeck.update-public-key.pem"), $rsa.ExportSubjectPublicKeyInfoPem(), [Text.UTF8Encoding]::new($false))
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestPath = Join-Path $root "dist\CyanDeck-$version-$Runtime.update.json"
$manifest = [ordered]@{
  version = $version
  schema = $schema
  runtime = $Runtime
  packageUrl = [IO.Path]::GetFileName($zip)
  sha256 = $hash
  releaseNotesUrl = ""
  notes = "CyanDeck $version ($Runtime)"
  publishedUtc = [DateTime]::UtcNow.ToString("o")
  signatureAlgorithm = ""
  signature = ""
  keyId = ""
}

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
    "CyanDeck.UpdateManifest.v1",
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

if ($null -ne $rsa) {
  $manifest.signatureAlgorithm = "RSA-PSS-SHA256"
  $manifest.keyId = $keyId
  $payload = [Text.Encoding]::UTF8.GetBytes((Get-CanonicalManifestPayload $manifest))
  $signatureBytes = $rsa.SignData($payload, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss)
  $manifest.signature = [Convert]::ToBase64String($signatureBytes)
  $rsa.Dispose()
}

# Always write UTF-8 without a BOM: Windows PowerShell's -Encoding UTF8 adds
# one, and the in-app updater parses the manifest with System.Text.Json.
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Host "Packaged: $zip"
Write-Host "Update manifest: $manifestPath"
Write-Host "SHA-256: $hash"
if (-not [string]::IsNullOrWhiteSpace($keyId)) { Write-Host "Signed manifest key ID: $keyId" }

} finally {
  if (Test-Path -LiteralPath $releasePublicPemPath -PathType Leaf) {
    Remove-Item -LiteralPath $releasePublicPemPath -Force
  }
}
