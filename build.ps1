param(
  [ValidateSet("win-x64", "win-arm64")]
  [string]$Runtime = "win-x64",
  [switch]$RequireGoogleOAuth,
  [string]$ReleasePublicKeyPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\CyanDeck\CyanDeck.csproj"
$out = Join-Path $root "publish\$Runtime"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw ".NET 8 SDK가 필요합니다: https://dotnet.microsoft.com/download/dotnet/8.0"
}

# The distributor places the downloaded Google *Desktop application* JSON here.
# The build input is embedded by CyanDeck.csproj and is never copied to publish/.
$oauthDesktopJson = Join-Path $root 'json\client_secret.json'
$oauthClientId = $null
if (Test-Path -LiteralPath $oauthDesktopJson -PathType Leaf) {
  try {
    $credentials = Get-Content -LiteralPath $oauthDesktopJson -Raw -Encoding UTF8 | ConvertFrom-Json
    $installed = $credentials.installed
    $id = [string]$installed.client_id
    $secret = [string]$installed.client_secret
    if ($null -eq $installed -or
        $id -notmatch '^[0-9]+-[A-Za-z0-9_-]+\.apps\.googleusercontent\.com$' -or
        [string]::IsNullOrWhiteSpace($secret)) {
      throw 'Expected installed.client_id and installed.client_secret from a Google Desktop OAuth client.'
    }
    $oauthClientId = $id
  } catch {
    # Do not echo the downloaded JSON, its contents or token values.
    throw 'Invalid json/client_secret.json: download OAuth credentials for application type Desktop app, not Web application.'
  }
  Write-Host 'Google Desktop OAuth credentials validated for embedding in CyanDeck.exe.'
} elseif ($RequireGoogleOAuth) {
  throw 'Required file missing: json/client_secret.json. Download Desktop app OAuth JSON from Google Cloud, place it inside the json folder, then double-click build.cmd again.'
} else {
  Write-Warning 'json/client_secret.json absent. Building an UNPROVISIONED development executable (Google sign-in not ready). Double-click build.cmd to enforce credential provisioning.'
}

# Publish overwrites known output files. Never recursively delete the output
# folder: developers sometimes keep unrelated data beside CyanDeck.exe.
New-Item -ItemType Directory -Force -Path $out | Out-Null

$publishArgs = @('publish', $project, '-c', 'Release', '-r', $Runtime,
  '--self-contained', 'true', '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
  '-o', $out)
if (-not [string]::IsNullOrWhiteSpace($ReleasePublicKeyPath)) {
  if (-not (Test-Path -LiteralPath $ReleasePublicKeyPath -PathType Leaf)) {
    throw 'Release verification public key was not generated before building.'
  }
  $publishArgs += ('-p:CyanDeckReleasePublicKeyPath=' + [IO.Path]::GetFullPath($ReleasePublicKeyPath))
}
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $out "CyanDeck.exe"
if (-not (Test-Path $exe)) {
  throw "Build completed without the expected executable: $exe"
}

$publicOAuthId = Join-Path $root "cyandeck-google-client-id.txt"
$publishedOAuthId = Join-Path $out "cyandeck-google-client-id.txt"
if (-not [string]::IsNullOrWhiteSpace($oauthClientId)) {
  # The downloaded Desktop OAuth JSON is authoritative. An obsolete public ID
  # from an older source snapshot must never be paired with the new secret.
  [System.IO.File]::WriteAllText($publishedOAuthId, $oauthClientId + [Environment]::NewLine,
      [System.Text.UTF8Encoding]::new($false))
  Write-Host 'Published matching public Google Desktop OAuth client ID (derived from embedded configuration).'
} elseif (Test-Path -LiteralPath $publicOAuthId) {
  $candidate = (Get-Content -LiteralPath $publicOAuthId -Raw -Encoding UTF8).Trim().TrimStart([char]0xFEFF)
  if ($candidate -notmatch '^[0-9]+-[A-Za-z0-9_-]+\.apps\.googleusercontent\.com$') {
    throw 'Invalid cyandeck-google-client-id.txt: expected a single Google Desktop OAuth client ID, never a user token.'
  }
  Copy-Item -LiteralPath $publicOAuthId -Destination $publishedOAuthId -Force
  Write-Host 'Published legacy public Google Desktop OAuth client ID (unprovisioned build).'
}

# MSBuild embeds validated Desktop OAuth credentials from json/client_secret.json
# as an assembly resource. Never copy the JSON to publish/ or end-user PCs.
if (-not [string]::IsNullOrWhiteSpace($oauthClientId)) {
  Write-Host 'Desktop OAuth config embedded in EXE. No runtime JSON required.'
}

Write-Host "Built: $exe"
