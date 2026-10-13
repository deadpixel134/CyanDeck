$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cyandeck-launch-preview-' + [Guid]::NewGuid().ToString('N'))
$previousOutput = $env:CYANDECK_LAUNCH_FIXTURE_OUTPUT
try {
  $payload = Join-Path $root 'portable'
  $fixtureOut = Join-Path $root 'fixture-build'
  $otherCwd = Join-Path $root 'other-working-directory'
  New-Item -ItemType Directory -Path $payload -Force | Out-Null
  New-Item -ItemType Directory -Path $fixtureOut -Force | Out-Null
  New-Item -ItemType Directory -Path $otherCwd -Force | Out-Null
  & (Join-Path $PSScriptRoot 'build-cyandeck-launcher-preview.ps1') -Runtime win-x64 -OutputDirectory $payload
  & dotnet publish (Join-Path $PSScriptRoot '../tests/CyanDeckLauncherFixture/CyanDeckLauncherFixture.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $fixtureOut
  if ($LASTEXITCODE -ne 0) { throw 'Fixture executable compilation failed.' }
  $fixtureExe = Join-Path $fixtureOut 'DesktopDeck.exe'
  if (-not (Test-Path -LiteralPath $fixtureExe -PathType Leaf)) {
    throw 'Fixture did not produce DesktopDeck.exe.'
  }
  Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $payload 'DesktopDeck.exe')
  $output = Join-Path $root 'received.json'
  $env:CYANDECK_LAUNCH_FIXTURE_OUTPUT = $output
  $launcher = Join-Path $payload 'CyanDeck.exe'
  $process = Start-Process -FilePath $launcher -WorkingDirectory $otherCwd -ArgumentList @('--peek-toggle', '--safe-mode', '--recover-groups-at-cursor') -PassThru -Wait
  if ($process.ExitCode -ne 0) { throw "Launcher exited unsuccessfully: $($process.ExitCode)" }
  for ($i = 0; $i -lt 60 -and -not (Test-Path -LiteralPath $output -PathType Leaf); $i++) {
    Start-Sleep -Milliseconds 100
  }
  if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
    throw 'CyanDeck.exe did not launch its sibling DesktopDeck.exe.'
  }
  $record = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
  if (@($record.Args).Count -ne 3 -or $record.Args[0] -ne '--peek-toggle' -or
      $record.Args[1] -ne '--safe-mode' -or $record.Args[2] -ne '--recover-groups-at-cursor') {
    throw 'CyanDeck.exe lost or reordered startup arguments.'
  }
  if (-not [string]::Equals([IO.Path]::GetFullPath($record.WorkingDirectory).TrimEnd('\','/'),
      [IO.Path]::GetFullPath($payload).TrimEnd('\','/'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CyanDeck.exe did not pin the companion application working directory.'
  }
  if ($record.ExecutableName -ne 'DesktopDeck.exe') {
    throw "CyanDeck.exe started an unexpected executable: $($record.ExecutableName)"
  }
  if (Test-Path -LiteralPath (Join-Path $payload 'calendar-events.json')) {
    throw 'Launcher unexpectedly created persistent calendar data.'
  }
  Write-Host 'CyanDeck.exe forwarded exact arguments and working directory to sibling DesktopDeck.exe.'
}
finally {
  $env:CYANDECK_LAUNCH_FIXTURE_OUTPUT = $previousOutput
  if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
  }
}
