#!/usr/bin/env python3
"""Phase-1 rebrand gate: cosmetic CyanDeck identity, stable legacy update/data contracts."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def source(path):
    return (root / path).read_text(encoding="utf-8-sig")

project = source("src/DesktopDeck/DesktopDeck.csproj")
settings = source("src/DesktopDeck/Views/SettingsWindow.xaml")
calendar = source("src/DesktopDeck/Calendar/CalendarWindow.cs")
tray = source("src/DesktopDeck/Services/TrayIconController.cs")
readme = source("README.md")
checks = {
    "product label is CyanDeck": "<Product>CyanDeck</Product>" in project and "<Title>CyanDeck</Title>" in project,
    "window name is CyanDeck": 'Title="CyanDeck 설정"' in settings,
    "calendar name is CyanDeck": 'Title = "CyanDeck Calendar"' in calendar,
    "tray label is CyanDeck": 'Text = "CyanDeck"' in tray,
    "README documents legacy compatibility": readme.startswith("# CyanDeck") and "%APPDATA%\\DesktopDeck" in readme,
    "assembly name stays DesktopDeck": "<AssemblyName>DesktopDeck</AssemblyName>" in project,
    "root namespace stays DesktopDeck": "<RootNamespace>DesktopDeck</RootNamespace>" in project,
    "existing executable path is preserved": 'Join-Path $installDir "DesktopDeck.exe"' in source("install.ps1"),
    "existing state folder is preserved": '"DesktopDeck"' in source("src/DesktopDeck/Services/StateStore.cs"),
    "existing single-instance mutex is preserved": 'Local\\DesktopDeck.SingleInstance' in source("src/DesktopDeck/App.xaml.cs"),
    "existing activation IPC is preserved": 'DesktopDeck.Activation.v1' in source("src/DesktopDeck/App.xaml.cs"),
    "OAuth embedded resource identifier is preserved": 'DesktopDeck.OAuthDesktopClient' in source("src/DesktopDeck/Calendar/GoogleOAuthClientConfiguration.cs"),
    "signed update canonical protocol is preserved": 'DesktopDeck.UpdateManifest.v1' in source("src/DesktopDeck/Services/UpdateService.cs"),
    "signed update still requires DesktopDeck.exe": 'The update ZIP does not contain DesktopDeck.exe.' in source("src/DesktopDeck/Services/UpdateService.cs"),
    "GitHub update channel is preserved": 'github-release://deadpixel134/DesktopDeck' in source("src/DesktopDeck/Services/GitHubReleaseSource.cs"),
}
# Phase 1.5: inspect direct UI/error copy, without renaming on-disk or IPC identities.
controller = source("src/DesktopDeck/Services/AppController.cs")
fence_menu = source("src/DesktopDeck/Views/FenceWindow.ContextMenu.cs")
swap = source("src/DesktopDeck/Layout/MonitorContentSwapCoordinator.cs")
state_schema = source("src/DesktopDeck/Services/StateSchema.cs")
state_store = source("src/DesktopDeck/Services/StateStore.cs")
calendar_backup_ui = source("src/DesktopDeck/Calendar/CalendarWindow.cs")
checks.update({
    "fence context settings item displays CyanDeck": 'MakeItem("CyanDeck 설정…"' in fence_menu and 'MakeItem("DesktopDeck 설정…"' not in fence_menu,
    "managed policy notice displays CyanDeck": "현재 CyanDeck 관리 정책에 의해" in controller and "현재 DesktopDeck 관리 정책에 의해" not in controller,
    "monitor swap user notices display CyanDeck": "교환할 CyanDeck 내용이 없습니다." in controller and "교환할 DesktopDeck 내용이 없습니다." not in controller,
    "feature toggle error displays CyanDeck": 'ReportFeatureSwitchSaveFailure("CyanDeck", ex)' in controller,
    "pending update recovery notice displays CyanDeck": "이전 CyanDeck 업데이트가 완료되지 않아" in controller and "이전 DesktopDeck 업데이트가 완료되지 않아" not in controller,
    "checkpoint restore notice displays CyanDeck": "이 CyanDeck 복구 체크포인트를 복원하시겠습니까?" in controller,
    "swap coordinator result copy displays CyanDeck": all(fragment in swap for fragment in (
        "CyanDeck could not confirm", "CyanDeck could not capture", "No movable CyanDeck content",
        "CyanDeck could not create", "CyanDeck could not re-confirm",
        "The swap failed and CyanDeck restored"))
        and not any(fragment in swap for fragment in (
        "DesktopDeck could not", "No movable DesktopDeck content", "DesktopDeck restored")),
    "unsupported state exception displays CyanDeck": "This CyanDeck configuration uses state schema" in state_schema
        and "Update CyanDeck before importing it." in state_schema
        and "public const int Current = 35;" in state_schema,
    "import error displays CyanDeck": "The selected CyanDeck backup could not be read." in state_store,
    "calendar backup note keeps exact legacy folder": r"CyanDeck의 기존 데이터 폴더(%APPDATA%\\DesktopDeck)" in calendar_backup_ui
        and r'"DesktopDeck", "calendar-events.json"' in calendar_backup_ui,
    "Windows taskbar AppUserModelID remains unchanged": '"DesktopDeck.Peek"' in source("src/DesktopDeck/Peek/WpfPeekTaskbarButtonWindow.cs"),
    "Google token path remains legacy DesktopDeck": '"DesktopDeck", "calendar-google-token.dat"' in source("src/DesktopDeck/Calendar/GoogleCalendarSync.cs"),
    "manual smoke checklist exists": "Windows 수동 스모크 테스트" in source("docs/CYANDECK_PHASE15_WINDOWS_SMOKE_KO.md"),
    "calendar LocalOnly behavior remains protected": 'LocalOnly' in source("src/DesktopDeck/Calendar/CalendarEventStore.cs")
        and 'LocalOnly' in source("src/DesktopDeck/Calendar/GoogleCalendarSync.cs"),
})
# Phase 2: installer/uninstaller copy must not rename compatibility identifiers.
installer = source("install.ps1")
uninstaller = source("uninstall.ps1")
checks.update({
    "installer shortcut description is CyanDeck": installer.count('$link.Description = "CyanDeck desktop organizer"') == 2 and '$cyanLink.Description = "CyanDeck desktop organizer"' in installer,
    "installer confirmation displays CyanDeck": 'Write-Host "Installed CyanDeck to: $installDir"' in installer,
    "uninstaller confirmation displays CyanDeck": "Write-Host 'CyanDeck was uninstalled" in uninstaller,
    "new CyanDeck Start Menu entry is separate": '"CyanDeck.lnk"' in installer and "'CyanDeck.lnk'" in uninstaller,
    "new shortcut ownership is checked before file copy": installer.index("Assert-ShortcutBelongsToDesktopDeck $cyanShortcut $expectedExe") < installer.index("Copy-Item $source"),
    "legacy and new Start Menu entries share EXE": '$cyanLink.TargetPath = Join-Path $installDir "DesktopDeck.exe"' in installer,
    "uninstaller only removes owned CyanDeck shortcut": '@($startMenuShortcut, $cyanStartMenuShortcut, $desktopShortcut)' in uninstaller and 'OrdinalIgnoreCase' in uninstaller,
    "legacy start menu shortcut filename preserved": '"DesktopDeck.lnk"' in installer and "'DesktopDeck.lnk'" in uninstaller,
    "legacy startup registry value preserved": '-Name "DesktopDeck"' in installer and "-Name 'DesktopDeck'" in uninstaller,
    "legacy install folder preserved": 'Programs\\DesktopDeck' in installer and 'Programs\\DesktopDeck' in uninstaller,
    "data purge requires two switches": '$PurgeData -and -not $ConfirmPurgeData' in uninstaller,
})
# Keep the original icon and Windows integration identities unchanged.
checks.update({
    "shortcut icon is inherited from existing EXE": '$link.IconLocation' not in installer and '$cyanLink.IconLocation' not in installer,
    "legacy desktop shortcut is not renamed": '$desktopShortcut = Join-Path ([Environment]::GetFolderPath(\'Desktop\')) "DesktopDeck.lnk"' in installer,
    "CyanDeck start menu shortcut is ownership validated": 'Assert-ShortcutBelongsToDesktopDeck $cyanShortcut $expectedExe' in installer,
    "legacy shortcut remains available for existing pins": '$link = $wsh.CreateShortcut($shortcut)' in installer,
    "new and legacy shortcuts share working directory": '$cyanLink.WorkingDirectory = $installDir' in installer and '$link.WorkingDirectory = $installDir' in installer,
    "uninstaller retains reparse-point protection": "ReparsePoint" in uninstaller and "ReparsePoint" in installer,
})
# Release branding changes the public title only, not signed asset filenames.
release_workflow = source(".github/workflows/release.yml")
checks.update({
    "release title displays CyanDeck": '--title "CyanDeck $TAG"' in release_workflow,
    "release assets keep DesktopDeck identity": 'dist/DesktopDeck-*.zip' in release_workflow and 'dist/DesktopDeck-*.update.json' in release_workflow,
    "release tag is still version validated": "Tag must exactly match version" in release_workflow,
    "release signing and OAuth secrets remain required": "DESKTOPDECK_UPDATE_SIGNING_KEY_PEM" in release_workflow and "DESKTOPDECK_GOOGLE_DESKTOP_OAUTH_JSON" in release_workflow,
    "application icon path stays unchanged": "<ApplicationIcon>AppIcon.ico</ApplicationIcon>" in project,
})
# Phase 2.3: update errors show CyanDeck without altering signed package identity.
update_service = source("src/DesktopDeck/Services/UpdateService.cs")
apply_update = source("apply-update.ps1")
checks.update({
    "update recovery errors display CyanDeck": "CyanDeck update recovery script is missing" in update_service,
    "update install errors display CyanDeck": "apply-update.ps1 is missing from the CyanDeck installation." in update_service,
    "recovery success displays CyanDeck": "CyanDeck recovery verified." in apply_update,
    "update process wait displays CyanDeck": "CyanDeck did not finish closing;" in apply_update,
    "update active process warning displays CyanDeck": "CyanDeck is still running." in apply_update,
    "update process executable remains DesktopDeck": "Get-Process -Name DesktopDeck" in apply_update,
    "signed manifest protocol stays unchanged": '"DesktopDeck.UpdateManifest.v1"' in update_service,
    "staged update storage stays unchanged": '"DesktopDeck", "updates", "staged"' in update_service,
})
# Phase 2.4: guard the update/recovery safety boundaries against regressions.
checks.update({
    "recovery journal remains in legacy storage": "'DesktopDeck\\\\updates'" in apply_update,
    "update checks executable before replacing files": "Assert-ManagedFile $InstallDir 'DesktopDeck.exe'" in apply_update,
    "update process waits for graceful exit": "Wait-DesktopDeckProcess $WaitForPid" in apply_update,
    "update rejects multiple executable payloads": "The package must contain exactly one DesktopDeck.exe." in apply_update,
    "rollback snapshot requires original executable": "The rollback snapshot is missing DesktopDeck.exe." in apply_update,
    "update maintains separate staging directory": ".DesktopDeck-stage-" in apply_update,
    "update script does not forcibly kill process": "Stop-Process" not in apply_update and "taskkill" not in apply_update.lower(),
    "update download validates executable presence": "The update ZIP does not contain DesktopDeck.exe." in update_service,
    "update signature remains required by default": "bool requireSignature = true" in update_service,
    "update manifest still uses RSA verification": "VerifyManifestSignature" in update_service,
})
# Migration preparation is opt-in and must not silently change live data roots.
migration = source("tools/prepare-cyandeck-data-migration.ps1")
checks.update({
    "migration defaults to dry run": "[switch]$Apply" in migration and "if (-not $Apply)" in migration,
    "migration refuses destination overwrite": "Destination already exists; refusing to merge or overwrite" in migration,
    "migration retains legacy data": "Original DesktopDeck data remains untouched" in migration,
    "migration verifies copied files": "Get-FileHash" in migration and "SHA256" in migration,
    "migration rejects reparse points": "ReparsePoint" in migration,
    "migration requires app exit": "Get-Process -Name DesktopDeck" in migration,
    "migration does not activate new path": "does NOT activate the new location" in migration,
})
migration_preflight = source("tools/prepare-cyandeck-data-migration.ps1")
checks.update({
    "migration validates layout before copying": "Migration preflight failed for $name" in migration_preflight and migration_preflight.index("Layout validation failed:") < migration_preflight.index("New-Item -ItemType Directory -Path $staging"),
    "migration rejects unsupported layout schema": "Unsupported layout schema version." in migration_preflight,
    "migration validates calendar array": "Calendar must be an array." in migration_preflight,
    "migration validates calendar before copying": migration_preflight.index("Migration preflight failed for $name") < migration_preflight.index("New-Item -ItemType Directory -Path $staging"),
})
checks.update({
    "migration checks layout backup and staged state": "'layout.json.bak'" in migration_preflight and "'layout.json.tmp'" in migration_preflight,
    "migration checks calendar backup and staged state": "'calendar-events.json.bak'" in migration_preflight and "'calendar-events.json.tmp'" in migration_preflight,
    "migration rejects malformed calendar entries": "Calendar event must be an object." in migration_preflight,
    "migration refuses invalid schema text": "[int]::TryParse" in migration_preflight,
})
checks.update({
    "migration records original hashes": "$sourceManifest[$relative]" in migration_preflight,
    "migration rejects changed source content": "Source changed during migration:" in migration_preflight,
    "migration rejects changed source file set": "Source file set changed during migration" in migration_preflight,
    "migration verifies before publish": migration_preflight.index("Source file set changed during migration") < migration_preflight.index("Move-Item -LiteralPath $staging"),
})
google_sync_contract = source("src/DesktopDeck/Calendar/GoogleCalendarSync.cs")
root_contract = source("src/DesktopDeck/Services/DataRootPaths.cs")
checks.update({
    "google token follows shared root": 'Path.Combine(DataRootPaths.ActiveRoaming, "calendar-google-token.dat")' in google_sync_contract,
    "google owner follows shared root": 'Path.Combine(DataRootPaths.ActiveRoaming, "calendar-google-owner.dat")' in google_sync_contract,
    "migration does not activate new roaming root": "ActiveRoaming => LegacyRoaming" in root_contract,
})
prepared_root_contract = source("src/DesktopDeck/Services/DataRootPaths.cs")
checks.update({
    "prepared root is detection only": "HasPreparedCyanRoamingCopy" in prepared_root_contract and "Directory.Exists(CyanRoaming)" in prepared_root_contract,
    "prepared root requires layout file": 'File.Exists(Path.Combine(CyanRoaming, "layout.json"))' in prepared_root_contract,
    "prepared root never changes active path": "ActiveRoaming => LegacyRoaming" in prepared_root_contract,
})
for label, passed in checks.items():
    print(("PASS" if passed else "FAIL") + ": " + label)
print(f"CyanDeck phase-1 compatibility: {sum(checks.values())}/{len(checks)}")
if not all(checks.values()):
    raise SystemExit(1)
