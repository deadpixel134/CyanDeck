param(
  [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
  [Parameter(Mandatory=$true)][string]$NativeExecutable,
  [Parameter(Mandatory=$true)][string]$SigningKeyPath,
  [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out=[IO.Path]::GetFullPath($OutputDirectory)
$exe=[IO.Path]::GetFullPath($NativeExecutable)
$key=[IO.Path]::GetFullPath($SigningKeyPath)
if (([IO.Path]::GetFileName($exe) -cne 'CyanDeck.exe') -or
    -not (Test-Path -LiteralPath $exe -PathType Leaf) -or
    -not (Test-Path -LiteralPath $key -PathType Leaf)) { throw 'Candidate executable or signing key missing.' }
if ((Get-Item -LiteralPath $exe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Native executable is a link.' }
if ((Get-Item -LiteralPath $key -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Signing key is a link.' }
if (Test-Path -LiteralPath $out) { throw 'Candidate destination already exists; no files will be overwritten.' }
$cur=$out
while ($cur) {
  if (Test-Path -LiteralPath $cur) {
    if ((Get-Item -LiteralPath $cur -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
      throw 'Candidate output ancestor is redirected.'
    }
  }
  $next=[IO.Path]::GetDirectoryName($cur.TrimEnd('\','/'))
  if (-not $next -or $next -eq $cur) { break }
  $cur=$next
}
foreach ($blocked in @(
    (Join-Path $root 'src'),(Join-Path $root '.git'),(Join-Path $root 'publish'),
    (Join-Path $env:APPDATA 'DesktopDeck'),(Join-Path $env:APPDATA 'CyanDeck'),
    (Join-Path $env:LOCALAPPDATA 'DesktopDeck'),(Join-Path $env:LOCALAPPDATA 'CyanDeck'),
    (Join-Path $env:LOCALAPPDATA 'Programs/DesktopDeck'),
    (Join-Path $env:LOCALAPPDATA 'Programs/CyanDeck'))) {
  $prefix=[IO.Path]::GetFullPath($blocked).TrimEnd('\','/')
  if ([string]::Equals($out,$prefix,[StringComparison]::OrdinalIgnoreCase) -or
      $out.StartsWith($prefix+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Signed candidate may not overwrite application files or user data.'
  }
}
[xml]$proj=Get-Content -LiteralPath (Join-Path $root 'src/DesktopDeck/DesktopDeck.csproj') -Raw
$version=[string]$proj.Project.PropertyGroup.Version
$exeVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
    ($exeVersion -ne $version -and -not $exeVersion.StartsWith($version+'.',[StringComparison]::Ordinal))) {
  throw 'CyanDeck candidate binary version differs from the source.'
}
$rsa=[Security.Cryptography.RSA]::Create()
$temp=Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-sign-' + [Guid]::NewGuid().ToString('N'))
try {
  $rsa.ImportFromPem((Get-Content -LiteralPath $key -Raw))
  $public=$rsa.ExportSubjectPublicKeyInfo()
  $keyId=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($public)).ToLowerInvariant()
  New-Item -ItemType Directory -Path $temp -ErrorAction Stop | Out-Null
  Copy-Item -LiteralPath $exe -Destination (Join-Path $temp 'CyanDeck.exe') -ErrorAction Stop
  $notice=@(
    'CYANDECK SIGNED NATIVE RELEASE CANDIDATE - NOT AN INSTALLER OR UPDATE'
    "Source version: $version; runtime: $Runtime"
    'This is the complete CyanDeck WPF application, not the launcher.'
    'Native preview compatibility still writes %APPDATA%\DesktopDeck.'
    'INSTALL / SIGNED UPDATE / DATA MIGRATION / ROLLBACK CUTOVER NOT ENABLED.'
    'Google OAuth configuration may be embedded in this distributor build.'
  ) -join [Environment]::NewLine
  [IO.File]::WriteAllText((Join-Path $temp 'CANDIDATE-NOTICE.txt'),$notice+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
  New-Item -ItemType Directory -Path $out -ErrorAction Stop | Out-Null
  $zipName="CyanDeck-$version-$Runtime-native-candidate.zip"
  $zip=Join-Path $out $zipName
  Compress-Archive -LiteralPath (Join-Path $temp 'CyanDeck.exe'),(Join-Path $temp 'CANDIDATE-NOTICE.txt') -DestinationPath $zip -CompressionLevel Optimal
  $digest=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  $canonical=[string]::Join([char]10,@('CyanDeck.NativeCandidate.v1',
    "version=$version","runtime=$Runtime","archiveName=$zipName","sha256=$digest"))
  $sig=$rsa.SignData([Text.Encoding]::UTF8.GetBytes($canonical),
    [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
  $manifest=[ordered]@{
    format='CyanDeck.NativeCandidate.v1';version=$version;runtime=$Runtime
    archiveName=$zipName;sha256=$digest;keyId=$keyId
    signatureAlgorithm='RSA-PSS-SHA256';signature=[Convert]::ToBase64String($sig)
  }
  $manifestFile=Join-Path $out 'candidate-manifest.json'
  [IO.File]::WriteAllText($manifestFile,($manifest | ConvertTo-Json -Compress),
    [Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $out 'candidate-public-key.pem'),
    $rsa.ExportSubjectPublicKeyInfoPem(),[Text.UTF8Encoding]::new($false))
  Write-Host "Signed private native candidate prepared (NOT a user installer): $zip"
}
finally {
  $rsa.Dispose()
  if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
