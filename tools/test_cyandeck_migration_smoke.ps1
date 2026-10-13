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
  & $script -VerifyPrepared
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
  # Reject malformed event entries in primary files and recovery candidates.
  # Top-level JSON arrays decode differently in Windows PowerShell 5.1 and 7.
  foreach ($badEventJson in @('[null]', '[7]', '["text"]', '[[]]', '{"wrong":"shape"}')) {
    Set-Content -LiteralPath (Join-Path $legacy 'calendar-events.json') -Value $badEventJson -Encoding UTF8
    $rejected = $false
    try { & $script -Apply -DestinationRoot $invalidDestination } catch { $rejected = $true }
    if (-not $rejected -or (Test-Path -LiteralPath $invalidDestination)) {
      throw "Migration accepted invalid calendar: $badEventJson"
    }
  }
  Set-Content -LiteralPath (Join-Path $legacy 'calendar-events.json') -Value '[]' -Encoding UTF8
  Set-Content -LiteralPath (Join-Path $legacy 'calendar-events.json.tmp') -Value '[null]' -Encoding UTF8
  $rejected = $false
  try { & $script -Apply -DestinationRoot $invalidDestination } catch { $rejected = $true }
  if (-not $rejected -or (Test-Path -LiteralPath $invalidDestination)) {
    throw 'Migration accepted null calendar event in recovery candidate.'
  }
  Remove-Item -LiteralPath (Join-Path $legacy 'calendar-events.json.tmp') -Force

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
  # A process can stop after publishing CyanDeck but before persisting
  # its preparation journal. Explicit recovery must verify, not overwrite.
  # An aborted journal permits a new preparation without altering either copy.
  $candidateLayout = Join-Path $cyan 'layout.json'
  $sourceLayout = Join-Path $legacy 'layout.json'
  Set-Content -LiteralPath $candidateLayout -Value '{"Version":34}' -Encoding UTF8
  $rejected = $false
  try { & $script -RecoverPreparation } catch { $rejected = $true }
  if (-not $rejected) { throw 'Recovery accepted a diverged CyanDeck layout.' }
  Copy-Item -LiteralPath $sourceLayout -Destination $candidateLayout -Force
  $extra = Join-Path $cyan 'unexpected-empty-directory'
  New-Item -ItemType Directory -Path $extra | Out-Null
  $rejected = $false
  try { & $script -RecoverPreparation } catch { $rejected = $true }
  if (-not $rejected) { throw 'Recovery accepted an extra CyanDeck directory.' }
  Remove-Item -LiteralPath $extra
  $nonCanonical = Join-Path $root 'CyanDeck-invalid-test'
  $rejected = $false
  try { & $script -RecoverPreparation -DestinationRoot $nonCanonical } catch { $rejected = $true }
  if (-not $rejected) { throw 'Recovery accepted a noncanonical destination.' }
  $rejected = $false
  try { & $script -RecoverPreparation -Apply } catch { $rejected = $true }
  if (-not $rejected) { throw 'Recovery accepted conflicting switches.' }
  # Invalid recovery candidates must fail even if the bytes in both profiles
  # match exactly; equality alone does not imply valid calendar data.
  $badBackup = Join-Path $legacy 'calendar-events.json.bak'
  $copyBadBackup = Join-Path $cyan 'calendar-events.json.bak'
  Set-Content -LiteralPath $badBackup -Value '[null]' -Encoding UTF8
  Copy-Item -LiteralPath $badBackup -Destination $copyBadBackup
  $rejected = $false
  try { & $script -RecoverPreparation } catch { $rejected = $true }
  if (-not $rejected) { throw 'Recovery accepted a malformed calendar backup.' }
  Remove-Item -LiteralPath $badBackup -Force
  Remove-Item -LiteralPath $copyBadBackup -Force

  # Simulate an interruption in the journal writer, then resume without
  # deleting or replacing the published data directory.
  Remove-Item -LiteralPath $journal -Force
  $lockPath = $journal + '.lock'
  # Existing lock file may remain; hold it with FileShare.None instead of
  # disturbing its contents. Recovery must fail without changing data.
  $lockHandle = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
  try {
    $rejected = $false
    try { & $script -RecoverPreparation } catch { $rejected = $true }
    if (-not $rejected) { throw 'Recovery ignored locked migration journal.' }
    if (Test-Path -LiteralPath $journal) { throw 'Failed recovery created a journal.' }
  } finally {
    $lockHandle.Dispose()
  }
  & $script -RecoverPreparation
  $entry = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
  if ($entry.Phase -ne 'prepared') { throw 'Recovery did not restore the prepared phase.' }
  if (-not (Test-Path -LiteralPath $candidateLayout) -or
      -not (Test-Path -LiteralPath $sourceLayout)) {
    throw 'Recovery deleted a profile.'
  }
  $rejected = $false
  try { & $script -RecoverPreparation } catch { $rejected = $true }
  if (-not $rejected) { throw 'Recovery improperly replaced an existing preparation journal.' }
  & $script -VerifyPrepared
  $entry = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
  if ($entry.Phase -ne 'verified') { throw 'Recovered preparation could not be verified.' }
  & $script -AbortPreparation
  # Also recover when an earlier valid aborted journal exists.
  & $script -RecoverPreparation
  $entry = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
  if ($entry.Phase -ne 'prepared') { throw 'Recovery refused an aborted journal.' }
  & $script -VerifyPrepared
  & $script -AbortPreparation
  # An old verified journal must block a fresh canonical candidate even
  # when the destination does not exist. Otherwise the test only exercises
  # the ordinary destination-exists guard, not journal preflight.
  Remove-Item -LiteralPath $cyan -Recurse -Force
  $entry = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
  $entry.Phase = 'verified'
  $entry | ConvertTo-Json -Compress | Set-Content -LiteralPath $journal -Encoding utf8
  $rejected = $false
  try { & $script -Apply } catch { $rejected = $true }
  if (-not $rejected -or (Test-Path -LiteralPath $cyan)) {
    throw 'Stale verified journal did not prevent publishing a new candidate.'
  }
  & $script -AbortPreparation
  # Reject oversized journal input before either preparation or abort can
  # interpret it; the legacy profile must remain untouched.
  $savedJournal = Get-Content -LiteralPath $journal -Raw -Encoding UTF8
  Set-Content -LiteralPath $journal -Value (' ' * 16385) -Encoding UTF8
  $rejected = $false
  try { & $script -Apply } catch { $rejected = $true }
  if (-not $rejected -or (Test-Path -LiteralPath $cyan)) {
    throw 'Oversized journal was accepted during migration preflight.'
  }
  $rejected = $false
  try { & $script -AbortPreparation } catch { $rejected = $true }
  if (-not $rejected) { throw 'Oversized journal was accepted during abort.' }
  Set-Content -LiteralPath $journal -Value $savedJournal -NoNewline -Encoding UTF8
  & $script -AbortPreparation
  # Failure injection at the exact publish/journal boundary: lock the journal
  # writer during -Apply, so the full copy publishes but metadata cannot.
  # Recovery must repair this genuine partial-publication state.
  $blockedJournal = [IO.FileStream]::new(($journal + '.lock'),
    [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
  $publishedWithError = $false
  try {
    try { & $script -Apply } catch {
      $publishedWithError = ([string]$_).Contains('-RecoverPreparation')
    }
  } finally { $blockedJournal.Dispose() }
  if (-not $publishedWithError -or
      -not (Test-Path -LiteralPath (Join-Path $cyan 'layout.json') -PathType Leaf)) {
    throw 'Journal failure injection did not leave a safely published copy.'
  }
  & $script -RecoverPreparation
  $entry = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
  if ($entry.Phase -ne 'prepared') {
    throw 'Recovery after a real publication failure did not restore prepared metadata.'
  }
  & $script -VerifyPrepared
  if (-not (Test-Path -LiteralPath (Join-Path $legacy 'layout.json') -PathType Leaf)) {
    throw 'Partial publication recovery altered legacy data.'
  }
  Write-Host 'CyanDeck migration smoke tests passed.'
}
finally {
  $env:APPDATA = $previousAppData
  $env:LOCALAPPDATA = $previousLocalAppData
  if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
  }
}
