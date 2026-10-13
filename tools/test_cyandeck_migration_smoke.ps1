$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-smoke-' + [Guid]::NewGuid().ToString('N'))
$previousAppData = $env:APPDATA
$previousLocalAppData = $env:LOCALAPPDATA
$script = Join-Path $PSScriptRoot 'prepare-cyandeck-data-migration.ps1'
try {
  $env:APPDATA = $root
  $env:LOCALAPPDATA = Join-Path $root 'local'
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
  $journal = Join-Path $env:LOCALAPPDATA 'DesktopDeck/migration/cyandeck-cutover-state.json'
  $entry = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
  if ($entry.Phase -ne 'verified' -or $entry.Version -ne 1) {
    throw 'Verified migration journal was not recorded.'
  }
  Add-Content -LiteralPath (Join-Path $legacy 'layout.json') -Value ' '
  $rejected = $false
  try { & $script -VerifyPrepared } catch { $rejected = $true }
  if (-not $rejected) { throw 'Verification accepted a diverged source.' }
  if (-not (Test-Path -LiteralPath (Join-Path $legacy 'layout.json'))) {
    throw 'Original profile was removed.'
  }
  # An existing candidate must never be overwritten or merged.
  $rejected = $false
  try { & $script -Apply } catch { $rejected = $true }
  if (-not $rejected) { throw 'Migration overwrote an existing CyanDeck profile.' }

  # Exercise schema preflight independently of the already prepared copy.
  $invalidDestination = Join-Path $root 'CyanDeck-invalid-test'
  Set-Content -LiteralPath (Join-Path $legacy 'layout.json') -Value '{"Version":999}' -Encoding utf8
  $rejected = $false
  try { & $script -Apply -DestinationRoot $invalidDestination } catch { $rejected = $true }
  if (-not $rejected -or (Test-Path -LiteralPath $invalidDestination)) {
    throw 'Migration accepted an unsupported layout schema.'
  }
  Set-Content -LiteralPath (Join-Path $legacy 'layout.json') -Value '{"Version":35}' -Encoding utf8
  Set-Content -LiteralPath (Join-Path $legacy 'calendar-events.json.bak') -Value '{invalid' -Encoding utf8
  $rejected = $false
  try { & $script -Apply -DestinationRoot $invalidDestination } catch { $rejected = $true }
  if (-not $rejected -or (Test-Path -LiteralPath $invalidDestination)) {
    throw 'Migration accepted a malformed calendar backup.'
  }
  if (-not (Test-Path -LiteralPath (Join-Path $legacy 'calendar-events.json.bak'))) {
    throw 'Migration removed the malformed original backup.'
  }
  # Simulate a running DesktopDeck by owning the same named mutex.
  # A second thread cannot enter the migration critical section.
  Remove-Item -LiteralPath (Join-Path $legacy 'calendar-events.json.bak') -Force
  $heldMutex = [Threading.Mutex]::new($false, 'Local\DesktopDeck.SingleInstance')
  $held = $false
  try {
    $held = $heldMutex.WaitOne(0)
    if (-not $held) { throw 'Could not acquire the test single-instance mutex.' }
    $blocked = $false
    $job = Start-Job -ScriptBlock {
      param($scriptPath, $appData, $candidate)
      $env:APPDATA = $appData
      & $scriptPath -Apply -DestinationRoot $candidate
    } -ArgumentList $script, $root, $invalidDestination
    try {
      if (-not (Wait-Job -Job $job -Timeout 30)) {
        Stop-Job -Job $job
        throw 'Mutex contention test timed out.'
      }
      if ($job.State -eq 'Failed') { $blocked = $true }
      Receive-Job -Job $job -ErrorAction SilentlyContinue | Out-Null
    } finally {
      Remove-Job -Job $job -Force
    }
    if (-not $blocked -or (Test-Path -LiteralPath $invalidDestination)) {
      throw 'Migration was not blocked by an active DesktopDeck mutex.'
    }
  } finally {
    if ($held) { $heldMutex.ReleaseMutex() }
    $heldMutex.Dispose()
  }
  # Once the other instance exits, a fresh migration must be able to
  # acquire the lock and prepare a clean copy without manual cleanup.
  & $script -Apply -DestinationRoot $invalidDestination
  if (-not (Test-Path -LiteralPath (Join-Path $invalidDestination 'layout.json') -PathType Leaf)) {
    throw 'Migration could not recover after mutex contention.'
  }
  & $script -VerifyPrepared -DestinationRoot $invalidDestination
  # Aborting only changes the journal; it must never delete either profile.
  & $script -AbortPreparation
  $entry = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
  if ($entry.Phase -ne 'aborted') { throw 'Abort phase was not recorded.' }
  & $script -AbortPreparation
  if (-not (Test-Path -LiteralPath (Join-Path $legacy 'layout.json')) -or
      -not (Test-Path -LiteralPath (Join-Path $cyan 'layout.json'))) {
    throw 'Abort removed source or candidate data.'
  }
  $rejected = $false
  try { & $script -Apply -AbortPreparation } catch { $rejected = $true }
  if (-not $rejected) { throw 'Conflicting abort and apply flags were accepted.' }
  # An old verified journal must block a fresh candidate before publication.
  $staleDestination = Join-Path $root 'CyanDeck-stale-journal-test'
  $entry = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
  $entry.Phase = 'verified'
  $entry | ConvertTo-Json -Compress | Set-Content -LiteralPath $journal -Encoding utf8
  $rejected = $false
  try { & $script -Apply -DestinationRoot $cyan } catch { $rejected = $true }
  if (-not $rejected) { throw 'Existing candidate unexpectedly accepted a second preparation.' }
  & $script -AbortPreparation
  Write-Host 'CyanDeck migration smoke tests passed.'
}
finally {
  $env:APPDATA = $previousAppData
  $env:LOCALAPPDATA = $previousLocalAppData
  if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
  }
}
