#!/usr/bin/env python3
"""Keep unsigned launcher previews separate from the signed legacy updater."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def read(path):
    return (root / path).read_text(encoding="utf-8-sig")

launch = read("src/CyanDeck.Launcher/Program.cs")
policy = read("src/CyanDeck.Launcher/CyanDeckLaunchPolicy.cs")
project = read("src/CyanDeck.Launcher/CyanDeck.Launcher.csproj")
package = read("package.ps1")
updater = read("apply-update.ps1")
rootpaths = read("src/DesktopDeck/Services/DataRootPaths.cs")
build = read("build.ps1")
checks = {
    "launcher is CyanDeck.exe": "<AssemblyName>CyanDeck</AssemblyName>" in project,
    "launcher uses original product icon": "<ApplicationIcon>../DesktopDeck/AppIcon.ico</ApplicationIcon>" in project,
    "fixed sibling legacy executable": 'Path.Combine(directory, "DesktopDeck.exe")' in policy,
    "launcher does not access user profiles": "APPDATA" not in launch + policy and "CyanRoaming" not in launch + policy,
    "launcher refuses profile activation": '"--activate-cyandeck"' in policy,
    "launcher passes arguments without a shell": "start.ArgumentList.Add(argument)" in launch and "UseShellExecute = false" in launch,
    "launcher refuses linked directories": "FileAttributes.ReparsePoint" in policy,
    "signed package remains legacy-only": '"DesktopDeck.exe"' in package and 'CyanDeck.Launcher' not in package and 'CyanDeck.exe' not in package,
    "legacy updater refuses uncoordinated CyanDeck.exe": "Update ZIP includes a non-application file." in updater and "'DesktopDeck.exe'" in updater and "'CyanDeck.exe'" not in updater,
    "original build unchanged": "src\\DesktopDeck\\DesktopDeck.csproj" in build and "CyanDeck.Launcher" not in build,
    "legacy roaming/local storage unchanged": "ActiveRoaming => LegacyRoaming" in rootpaths and "ActiveLocal => LegacyLocal" in rootpaths,
}
for label, passed in checks.items():
    print(("PASS" if passed else "FAIL") + ": " + label)
print(f"CyanDeck launcher contract: {sum(checks.values())}/{len(checks)}")
if not all(checks.values()):
    raise SystemExit(1)
