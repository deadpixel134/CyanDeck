param(
  [Parameter(Mandatory=$true)][string]$ManifestPath,
  [string]$PublicKeyPath
)
$ErrorActionPreference='Stop'
$manifest=[IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Candidate manifest missing.' }
$dir=[IO.Path]::GetDirectoryName($manifest)
if (-not $PublicKeyPath) { $PublicKeyPath=Join-Path $dir 'candidate-public-key.pem' }
$record=Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
if ($record.format -cne 'CyanDeck.NativeCandidate.v1' -or
    $record.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
    $record.runtime -cnotin @('win-x64','win-arm64') -or
    $record.signatureAlgorithm -cne 'RSA-PSS-SHA256' -or
    $record.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    $record.keyId -cnotmatch '^[0-9a-f]{64}$') { throw 'Malformed candidate manifest.' }
$expected="CyanDeck-$($record.version)-$($record.runtime)-native-candidate.zip"
if ($record.archiveName -cne $expected) { throw 'Unsafe or unexpected archive filename.' }
$archive=Join-Path $dir $expected
if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or
    -not (Test-Path -LiteralPath $PublicKeyPath -PathType Leaf)) { throw 'Candidate archive or public key missing.' }
if ((Get-Item -LiteralPath $archive -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
  throw 'Candidate archive is a redirected file.'
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.sha256) {
  throw 'Candidate archive SHA-256 mismatch.'
}
$rsa=[Security.Cryptography.RSA]::Create()
try {
  $rsa.ImportFromPem((Get-Content -LiteralPath $PublicKeyPath -Raw))
  $keyHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    $rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
  if ($keyHash -cne $record.keyId) { throw 'Candidate verification key ID mismatch.' }
  $canonical=[string]::Join([char]10,@('CyanDeck.NativeCandidate.v1',
    "version=$($record.version)","runtime=$($record.runtime)",
    "archiveName=$($record.archiveName)","sha256=$($record.sha256)"))
  $signature=[Convert]::FromBase64String([string]$record.signature)
  if (-not $rsa.VerifyData([Text.Encoding]::UTF8.GetBytes($canonical),$signature,
      [Security.Cryptography.HashAlgorithmName]::SHA256,
      [Security.Cryptography.RSASignaturePadding]::Pss)) {
    throw 'Native candidate signature verification failed.'
  }
}
finally { $rsa.Dispose() }
Add-Type -AssemblyName System.IO.Compression
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try {
  if ($zip.Entries.Count -ne 2) { throw 'Candidate must contain exactly two files.' }
  $expectedNames=@('CyanDeck.exe','CANDIDATE-NOTICE.txt')
  foreach ($entry in $zip.Entries) {
    if ($entry.FullName -cnotin $expectedNames -or $entry.Length -le 0 -or
        (($entry.ExternalAttributes -shr 16) -band 0xA000) -eq 0xA000) {
      throw 'Unexpected or unsafe candidate ZIP entry.'
    }
  }
  $exeEntry=$zip.GetEntry('CyanDeck.exe')
  $noticeEntry=$zip.GetEntry('CANDIDATE-NOTICE.txt')
  if ($null -eq $exeEntry -or $null -eq $noticeEntry -or $exeEntry.Length -lt 10000) {
    throw 'Missing native EXE or candidate notice.'
  }
  $reader=[IO.StreamReader]::new($noticeEntry.Open())
  try { $notice=$reader.ReadToEnd() } finally { $reader.Dispose() }
  foreach ($phrase in @('NOT AN INSTALLER OR UPDATE',
    'CUTOVER NOT ENABLED','%APPDATA%\DesktopDeck')) {
    if (-not $notice.Contains($phrase)) { throw 'Candidate safety notice incomplete.' }
  }
}
finally { $zip.Dispose() }
Write-Host 'Signed CyanDeck native candidate digest, signature and ZIP contents verified.'
