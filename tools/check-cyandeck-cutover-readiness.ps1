param([switch]$Json)
# Strictly read-only. Does not write a cutover fence, advance a journal,
# replace binaries, change the active profile, or disclose private paths.
$ErrorActionPreference = 'Stop'
$reasons = [Collections.Generic.List[string]]::new()
$blocked = $false
$legacyExists = $false
$candidateExists = $false
$verified = $false
$journalPhase = 'absent'
function Add-Reason([string]$Code, [bool]$HardBlock = $false) {
  if (-not $reasons.Contains($Code)) { $reasons.Add($Code) }
  if ($HardBlock) { $script:blocked = $true }
}
function Assert-NoLinkAncestors([string]$Path) {
  $cursor = [IO.Path]::GetFullPath($Path)
  while ($cursor) {
    if (Test-Path -LiteralPath $cursor) {
      $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'UNSAFE_LINK'
      }
    }
    $next = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
    if (-not $next -or $next -eq $cursor) { break }
    $cursor = $next
  }
}
function Inspect-Profile([string]$Root) {
  Assert-NoLinkAncestors $Root
  if (-not (Test-Path -LiteralPath $Root)) { return $null }
  if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw 'BAD_ROOT' }
  $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
  $dirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  $pending = [Collections.Generic.Stack[string]]::new()
  $pending.Push($Root)
  $total = 0
  while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    Assert-NoLinkAncestors $directory
    foreach ($entry in (Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
      if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'UNSAFE_LINK' }
      $relative = $entry.FullName.Substring($Root.Length).TrimStart('\', '/')
      if (-not $relative) { throw 'INVALID_RELATIVE_PATH' }
      $total++
      if ($total -gt 100000) { throw 'PROFILE_ENTRY_LIMIT' }
      if ($entry.PSIsContainer) {
        if (-not $dirs.Add($relative)) { throw 'DUPLICATE_DIRECTORY' }
        $pending.Push($entry.FullName)
      } else {
        if ($files.ContainsKey($relative)) { throw 'DUPLICATE_FILE' }
        $files.Add($relative, (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256 -ErrorAction Stop).Hash)
      }
    }
  }
  return [pscustomobject]@{ Files = $files; Dirs = $dirs }
}
function Assert-ProfileJson([string]$Root, [int]$MaxSchema) {
  foreach ($name in @('layout.json','layout.json.bak','layout.json.tmp',
                       'calendar-events.json','calendar-events.json.bak','calendar-events.json.tmp')) {
    $item = Join-Path $Root $name
    if (-not (Test-Path -LiteralPath $item)) { continue }
    if (-not (Test-Path -LiteralPath $item -PathType Leaf)) { throw 'INVALID_ENTRY' }
    try {
      $raw = Get-Content -LiteralPath $item -Raw -Encoding UTF8 -ErrorAction Stop
      if ($name.StartsWith('calendar-events.json', [StringComparison]::OrdinalIgnoreCase)) {
        if (-not $raw.TrimStart().StartsWith('[')) { throw 'BAD_ARRAY' }
        $array = (ConvertFrom-Json -InputObject ('{"events":' + $raw + '}') -ErrorAction Stop).events
        if ($array -isnot [array]) { throw 'BAD_ARRAY' }
        foreach ($event in $array) {
          if ($null -eq $event -or $event -isnot [pscustomobject]) { throw 'BAD_EVENT' }
        }
      } else {
        $layout = ConvertFrom-Json -InputObject $raw -ErrorAction Stop
        if ($null -eq $layout -or $layout -isnot [pscustomobject]) { throw 'BAD_LAYOUT' }
        if ($null -ne $layout.Version) {
          $version = 0
          if (-not [int]::TryParse([string]$layout.Version, [ref]$version) -or
              $version -lt 0 -or $version -gt $MaxSchema) { throw 'BAD_SCHEMA' }
        }
      }
    } catch { throw 'INVALID_PROFILE_JSON' }
  }
}
function Equal-Profiles($Left, $Right) {
  if ($null -eq $Left -or $null -eq $Right) { return $false }
  if ($Left.Files.Count -ne $Right.Files.Count -or $Left.Dirs.Count -ne $Right.Dirs.Count) { return $false }
  foreach ($key in $Left.Files.Keys) {
    if (-not $Right.Files.ContainsKey($key) -or $Left.Files[$key] -cne $Right.Files[$key]) { return $false }
  }
  foreach ($key in $Left.Dirs) {
    if (-not $Right.Dirs.Contains($key)) { return $false }
  }
  return $true
}
function Write-Report {
  $condition = if ($blocked) { 'blocked' }
    elseif (-not $legacyExists) { 'no-legacy-profile' }
    elseif (-not $candidateExists) { 'needs-preparation' }
    elseif (-not $verified) { 'needs-verification' }
    else { 'verified-copy' }
  $result = [ordered]@{
    FormatVersion=1; Condition=$condition; ReadOnly=$true
    CutoverAllowed=$false; VerifiedCopyMatches=$verified
    JournalPhase=$journalPhase; ReasonCodes=@($reasons.ToArray())
  }
  if ($Json) { Write-Output ($result | ConvertTo-Json -Compress -Depth 4) }
  else {
    Write-Host "CyanDeck readiness: $condition (read-only; cutover disabled)"
    foreach ($reason in $reasons) { Write-Host "  $reason" }
  }
}
$held = [Collections.Generic.List[object]]::new()
try {
  if ([string]::IsNullOrWhiteSpace($env:APPDATA) -or [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    Add-Reason 'PROFILE_ENVIRONMENT_MISSING' $true
    return
  }
  # Hold BOTH locks during every scan. Older binaries may not hold the new
  # mutex, hence the additional process check before reading any profile.
  foreach ($name in @('Local\DesktopDeck.SingleInstance','Local\CyanDeck.LegacyProfileWriter.v1')) {
    $mutex = [Threading.Mutex]::new($false, $name)
    $owns = $false
    $abandoned = $false
    try { $owns = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $owns = $true; $abandoned = $true }
    if ($owns) { $held.Add($mutex) } else { $mutex.Dispose() }
    if ($abandoned) { Add-Reason 'ABANDONED_WRITER_LOCK' $true; return }
    if (-not $owns) { Add-Reason 'PROFILE_WRITER_ACTIVE' $true; return }
  }
  if (@(Get-Process -Name DesktopDeck,CyanDeck -ErrorAction SilentlyContinue).Count -gt 0) {
    Add-Reason 'APPLICATION_STILL_RUNNING' $true
    return
  }
  $legacy = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'DesktopDeck'))
  $candidate = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'CyanDeck'))
  $journal = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'DesktopDeck\migration\cyandeck-cutover-state.json'))
  $fence = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'DesktopDeck\migration\legacy-writer-fenced.json'))
  try {
    foreach ($path in @($legacy,$candidate,$journal,$fence)) { Assert-NoLinkAncestors $path }
    if (Test-Path -LiteralPath $fence) { Add-Reason 'LEGACY_CUTOVER_FENCE_PRESENT' $true }
  } catch { Add-Reason 'UNSAFE_PROFILE_OR_JOURNAL_PATH' $true; return }
  $source = $null
  $target = $null
  try { $source = Inspect-Profile $legacy; $legacyExists = $null -ne $source }
  catch { Add-Reason 'UNSAFE_OR_UNREADABLE_LEGACY_PROFILE' $true; return }
  try { $target = Inspect-Profile $candidate; $candidateExists = $null -ne $target }
  catch { Add-Reason 'UNSAFE_OR_UNREADABLE_CANDIDATE_PROFILE' $true; return }
  if (-not $legacyExists) {
    Add-Reason 'LEGACY_PROFILE_MISSING'
    if ($candidateExists) { Add-Reason 'ORPHANED_CANDIDATE' $true }
    return
  }
  $schemaFile = Join-Path $PSScriptRoot '../src/DesktopDeck/Services/StateSchema.cs'
  $match = [regex]::Match((Get-Content -LiteralPath $schemaFile -Raw -ErrorAction Stop),
                         'public\s+const\s+int\s+Current\s*=\s*(\d+)')
  if (-not $match.Success) { Add-Reason 'SCHEMA_NOT_IDENTIFIABLE' $true; return }
  $max = [int]$match.Groups[1].Value
  try { Assert-ProfileJson $legacy $max }
  catch { Add-Reason 'LEGACY_PROFILE_JSON_INVALID' $true }
  if ($candidateExists) {
    try { Assert-ProfileJson $candidate $max }
    catch { Add-Reason 'CANDIDATE_PROFILE_JSON_INVALID' $true }
  } else { Add-Reason 'PREPARATION_REQUIRED' }
  if (-not (Test-Path -LiteralPath $journal)) {
    if ($candidateExists) { Add-Reason 'CANDIDATE_WITHOUT_JOURNAL' $true }
    return
  }
  try {
    if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) { throw 'BAD_JOURNAL' }
    if ((Get-Item -LiteralPath $journal -Force).Length -gt 16384) { throw 'BAD_JOURNAL' }
    $record = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    $when = [DateTimeOffset]::MinValue
    $timeOk = [DateTimeOffset]::TryParse([string]$record.RecordedAtUtc,
      [Globalization.CultureInfo]::InvariantCulture,
      [Globalization.DateTimeStyles]::RoundtripKind, [ref]$when)
    if ($record.Version -ne 1 -or $record.Phase -notin @('prepared','verified','aborted') -or
        -not $timeOk -or $when -eq [DateTimeOffset]::MinValue -or
        $when -gt [DateTimeOffset]::UtcNow.AddMinutes(10) -or
        -not [string]::Equals([string]$record.LegacyRoot,$legacy,[StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$record.CandidateRoot,$candidate,[StringComparison]::OrdinalIgnoreCase)) {
      throw 'BAD_JOURNAL'
    }
    $journalPhase = [string]$record.Phase
  } catch { $journalPhase = 'invalid'; Add-Reason 'MALFORMED_MIGRATION_JOURNAL' $true; return }
  if ($journalPhase -ne 'verified') { Add-Reason 'PREPARATION_NOT_VERIFIED'; return }
  if (-not $candidateExists) { Add-Reason 'VERIFIED_JOURNAL_WITHOUT_CANDIDATE' $true; return }
  if (-not (Equal-Profiles $source $target)) { Add-Reason 'PROFILE_COPY_DIVERGED' $true; return }
  if (-not $blocked) { $verified = $true }
}
catch { Add-Reason 'READ_ONLY_AUDIT_FAILED' $true }
finally {
  for ($i = $held.Count - 1; $i -ge 0; $i--) {
    try { $held[$i].ReleaseMutex() } finally { $held[$i].Dispose() }
  }
  Write-Report
}
