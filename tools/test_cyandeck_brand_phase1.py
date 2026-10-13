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
for label, passed in checks.items():
    print(("PASS" if passed else "FAIL") + ": " + label)
print(f"CyanDeck phase-1 compatibility: {sum(checks.values())}/{len(checks)}")
if not all(checks.values()):
    raise SystemExit(1)
