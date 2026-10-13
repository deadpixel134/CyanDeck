$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-smoke-' + [Guid]::NewGuid().ToString('N'))
$previousAppData = $env:APPDATA
$script = Join-Path $PSScriptRoot 'prepare-cyandeck-data-migration.ps1'
try {
  $env:APPDATA = $root
  $legacy = Join-Path $root 'DesktopDeck'
  $cyan = Join-Path $root 'CyanDeck'
  New-Item -ItemType Directory -Path $legacy -Force | Out-Null
  Set-Content -LiteralPath (Join-Path $legacy 'layout.json') -Value '{"Version":35}' -Encoding utf8
  Set-Content -LiteralPath (Join-Path $legacy 'calendar-events.json') -Value '[]' -Encoding utf8
  New-Item -ItemType Directory -Path (Join-Path $legacy 'empty') | Out-Null
  & $script
  if (Test-Path -LiteralPath $cyan) { throw 'Dry run unexpectedly created a profile.' }
  & $script -Apply
  if (-not (Test-Path -LiteralPath (Join-Path $cyan 'empty') -PathType Container)) {
    throw 'Prepared copy lost an empty directory.'
  }
  & $script -VerifyPrepared
  Add-Content -LiteralPath (Join-Path $legacy 'layout.json') -Value ' '
  $rejected = $false
  try { & $script -VerifyPrepared } catch { $rejected = $true }
  if (-not $rejected) { throw 'Verification accepted a diverged source.' }
  if (-not (Test-Path -LiteralPath (Join-Path $legacy 'layout.json'))) {
    throw 'Original profile was removed.'
  }
  Write-Host 'CyanDeck migration smoke tests passed.'
}
finally {
  $env:APPDATA = $previousAppData
  if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
  }
}
