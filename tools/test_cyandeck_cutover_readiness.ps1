# Disposable Windows fixtures only: never mutate an existing user profile.
$ErrorActionPreference = 'Stop'
$base = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-audit-' + [Guid]::NewGuid().ToString('N'))
$previousApp = $env:APPDATA
$previousLocal = $env:LOCALAPPDATA
$audit = Join-Path $PSScriptRoot 'check-cyandeck-cutover-readiness.ps1'
$migrator = Join-Path $PSScriptRoot 'prepare-cyandeck-data-migration.ps1'
function Check([string]$Expected) {
  $value = & $audit -Json | ConvertFrom-Json
  if ($value.Condition -cne $Expected -or -not $value.ReadOnly -or $value.CutoverAllowed) {
    throw "Readiness result mismatch: expected $Expected, found $($value.Condition)"
  }
  return $value
}
function Require-Reason($Result,[string]$Reason) {
  if (@($Result.ReasonCodes) -cnotcontains $Reason) { throw "Missing reason code: $Reason" }
}
try {
  $env:APPDATA = Join-Path $base 'roaming'
  $env:LOCALAPPDATA = Join-Path $base 'local'
  New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
  New-Item -ItemType Directory -Path $env:LOCALAPPDATA -Force | Out-Null
  Require-Reason (Check 'no-legacy-profile') 'LEGACY_PROFILE_MISSING'
  $original = Join-Path $env:APPDATA 'DesktopDeck'
  $copy = Join-Path $env:APPDATA 'CyanDeck'
  New-Item -ItemType Directory -Path $original | Out-Null
  Set-Content -LiteralPath (Join-Path $original 'layout.json') -Value '{"Version":35}' -Encoding UTF8
  Set-Content -LiteralPath (Join-Path $original 'calendar-events.json') -Value '[]' -Encoding UTF8
  New-Item -ItemType Directory -Path (Join-Path $original 'empty') | Out-Null
  Require-Reason (Check 'needs-preparation') 'PREPARATION_REQUIRED'
  & $migrator -Apply
  Require-Reason (Check 'needs-verification') 'PREPARATION_NOT_VERIFIED'
  & $migrator -VerifyPrepared
  $report = Check 'verified-copy'
  if (-not $report.VerifiedCopyMatches -or $report.JournalPhase -ne 'verified') {
    throw 'Expected verified identical copy.'
  }
  $hash = (Get-FileHash -LiteralPath (Join-Path $original 'layout.json')).Hash
  $null = Check 'verified-copy'
  if ((Get-FileHash -LiteralPath (Join-Path $original 'layout.json')).Hash -cne $hash) {
    throw 'Readiness audit wrote to source data.'
  }
  Add-Content -LiteralPath (Join-Path $copy 'layout.json') -Value ' '
  Require-Reason (Check 'blocked') 'PROFILE_COPY_DIVERGED'
  Copy-Item -LiteralPath (Join-Path $original 'layout.json') -Destination (Join-Path $copy 'layout.json') -Force
  $journal = Join-Path $env:LOCALAPPDATA 'DesktopDeck\migration\cyandeck-cutover-state.json'
  $saved = Get-Content -LiteralPath $journal -Raw -Encoding UTF8
  Set-Content -LiteralPath $journal -Value '{"corrupt":true}' -Encoding UTF8
  Require-Reason (Check 'blocked') 'MALFORMED_MIGRATION_JOURNAL'
  Set-Content -LiteralPath $journal -Value $saved -NoNewline -Encoding UTF8
  $null = Check 'verified-copy'
  Set-Content -LiteralPath (Join-Path $original 'calendar-events.json') -Value '[null]' -Encoding UTF8
  Copy-Item -LiteralPath (Join-Path $original 'calendar-events.json') -Destination (Join-Path $copy 'calendar-events.json') -Force
  Require-Reason (Check 'blocked') 'LEGACY_PROFILE_JSON_INVALID'
  Set-Content -LiteralPath (Join-Path $original 'calendar-events.json') -Value '[]' -Encoding UTF8
  Copy-Item -LiteralPath (Join-Path $original 'calendar-events.json') -Destination (Join-Path $copy 'calendar-events.json') -Force
  $null = Check 'verified-copy'
  $fence = Join-Path $env:LOCALAPPDATA 'DesktopDeck\migration\legacy-writer-fenced.json'
  Set-Content -LiteralPath $fence -Value '{}' -Encoding UTF8
  Require-Reason (Check 'blocked') 'LEGACY_CUTOVER_FENCE_PRESENT'
  Remove-Item -LiteralPath $fence -Force
  $null = Check 'verified-copy'
  $linked = Join-Path $original 'linked'
  New-Item -ItemType Junction -Path $linked -Target (Join-Path $original 'empty') -ErrorAction Stop | Out-Null
  Require-Reason (Check 'blocked') 'UNSAFE_OR_UNREADABLE_LEGACY_PROFILE'
  [IO.Directory]::Delete($linked)
  $null = Check 'verified-copy'
  Write-Host 'CyanDeck cutover readiness fixture tests passed.'
}
finally {
  $env:APPDATA = $previousApp
  $env:LOCALAPPDATA = $previousLocal
  if (Test-Path -LiteralPath $base) { Remove-Item -LiteralPath $base -Recurse -Force }
}
