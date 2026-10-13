# True update/rollback integration using disposable locations. The updater
# is invoked in a child PowerShell process so exit 1 cannot stop the harness.
# Run under both PowerShell 7 and Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-update-layout-' + [Guid]::NewGuid().ToString('N'))
$previousRoaming = $env:APPDATA
$previousLocal = $env:LOCALAPPDATA
try {
  $env:APPDATA = Join-Path $root 'roaming'
  $env:LOCALAPPDATA = Join-Path $root 'local'
  $installed = Join-Path $root 'installed'
  New-Item -ItemType Directory -Path $installed -Force | Out-Null
  Set-Content -LiteralPath (Join-Path $installed 'DesktopDeck.exe') -Value 'old-exe' -Encoding ASCII
  Set-Content -LiteralPath (Join-Path $installed 'user-notes.txt') -Value 'unrelated user file' -Encoding ASCII
  $script = Join-Path $PSScriptRoot '../apply-update.ps1'
  $shell = Join-Path $PSHOME $(if ($PSVersionTable.PSVersion.Major -ge 6) { 'pwsh.exe' } else { 'powershell.exe' })
  Add-Type -AssemblyName System.IO.Compression.FileSystem

  function New-FixtureZip([string]$Name, [System.Collections.IDictionary]$Files) {
    $zip = Join-Path $root ($Name + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
      foreach ($name in $Files.Keys) {
        $entry = $archive.CreateEntry([string]$name)
        $stream = $entry.Open()
        try {
          $bytes = [Text.Encoding]::ASCII.GetBytes([string]$Files[$name])
          $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
      }
    } finally { $archive.Dispose() }
    return $zip
  }

  function Invoke-Update([string]$Zip, [bool]$ShouldSucceed) {
    $hash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
    $result = & $shell -NoProfile -ExecutionPolicy Bypass -File $script -InstallDir $installed -Package $Zip -ExpectedSha256 $hash 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if (($exitCode -eq 0) -ne $ShouldSucceed) {
      throw "Unexpected updater result for $Zip (exit=$exitCode): $result"
    }
  }

  $before = (Get-FileHash -LiteralPath (Join-Path $installed 'DesktopDeck.exe') -Algorithm SHA256).Hash
  $fixtures = @(
    @{ name='sibling-roots'; files=[ordered]@{ 'A/DesktopDeck.exe'='new-exe'; 'B/README.md'='ignored sibling' } },
    @{ name='mixed-root'; files=[ordered]@{ 'DesktopDeck.exe'='new-exe'; 'extra/README.md'='unrelated' } },
    @{ name='duplicate-entry'; files=[ordered]@{ 'DesktopDeck.exe'='new-exe'; 'README.md'='valid'; 'A/README.md'='mixed' } },
    @{ name='second-exe-root'; files=[ordered]@{ 'A/DesktopDeck.exe'='new-exe'; 'B/DesktopDeck.exe'='new-exe' } },
    @{ name='uncoordinated-exe'; files=[ordered]@{ 'DesktopDeck.exe'='new-exe'; 'CyanDeck.exe'='new-exe' } }
  )
  foreach ($fixture in $fixtures) {
    $zip = New-FixtureZip $fixture.name $fixture.files
    Invoke-Update $zip $false
    $after = (Get-FileHash -LiteralPath (Join-Path $installed 'DesktopDeck.exe') -Algorithm SHA256).Hash
    if ($after -cne $before) { throw "Rejected package changed installed EXE: $($fixture.name)" }
    if (-not (Test-Path -LiteralPath (Join-Path $installed 'user-notes.txt'))) {
      throw "Rejected package removed unrelated user file: $($fixture.name)"
    }
    $pending = @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'DesktopDeck\updates') -Filter 'pending-*.json' -File -ErrorAction SilentlyContinue)
    if ($pending.Count -ne 0) { throw "Rejected package left recovery journal: $($fixture.name)" }
  }

  # A normal wrapped signed-style package updates only its allowlisted files.
  $valid = New-FixtureZip 'valid-wrapped' ([ordered]@{
    'DesktopDeck-test-win-x64/DesktopDeck.exe'='new-exe'
    'DesktopDeck-test-win-x64/README.md'='test-only readme'
  })
  Invoke-Update $valid $true
  if ((Get-Content -LiteralPath (Join-Path $installed 'DesktopDeck.exe') -Raw).Trim() -ne 'new-exe') {
    throw 'Valid wrapped package was not installed.'
  }
  if ((Get-Content -LiteralPath (Join-Path $installed 'user-notes.txt') -Raw).Trim() -ne 'unrelated user file') {
    throw 'Updater changed an unrelated file.'
  }
  $rollbackMarker = Join-Path $env:LOCALAPPDATA 'DesktopDeck\updates\last-rollback.txt'
  if (-not (Test-Path -LiteralPath $rollbackMarker -PathType Leaf)) {
    throw 'Successful update did not record a rollback snapshot.'
  }
  $rollback = (Get-Content -LiteralPath $rollbackMarker -Raw).Trim()
  $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $script -InstallDir $installed -RollbackDir $rollback 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) { throw "Rollback failed: $output" }
  if ((Get-Content -LiteralPath (Join-Path $installed 'DesktopDeck.exe') -Raw).Trim() -ne 'old-exe') {
    throw 'Rollback did not restore the original executable.'
  }
  if (-not (Test-Path -LiteralPath (Join-Path $installed 'user-notes.txt') -PathType Leaf)) {
    throw 'Rollback removed unrelated user data.'
  }
  Write-Host 'CyanDeck ZIP layout and application-file rollback integration passed.'
}
finally {
  $env:APPDATA = $previousRoaming
  $env:LOCALAPPDATA = $previousLocal
  if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
