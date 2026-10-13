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
preview_builder = read("tools/build-cyandeck-launcher-preview.ps1")
preview_workflow = read(".github/workflows/build.yml")
checks = {
    "launcher is CyanDeck.exe": "<AssemblyName>CyanDeck</AssemblyName>" in project,
    "launcher uses original product icon": "<ApplicationIcon>../DesktopDeck/AppIcon.ico</ApplicationIcon>" in project,
    "fixed sibling legacy executable": 'Path.Combine(directory, "DesktopDeck.exe")' in policy,
    "launcher does not access user profiles": "CyanRoaming" not in launch + policy and "File.WriteAllText" not in launch + policy and "Directory.CreateDirectory" not in launch + policy and "CyanDeckLaunchPolicy.ResolveLegacyExecutable" in launch,
    "launcher refuses profile activation": '"--activate-cyandeck"' in policy,
    "launcher passes arguments without a shell": "start.ArgumentList.Add(arg)" in launch and "UseShellExecute = false" in launch and "CyanDeckLaunchPolicy.ValidateArguments(args)" in launch,
    "launcher refuses linked directories": "FileAttributes.ReparsePoint" in policy,
    "signed package remains legacy-only": "'DesktopDeck.exe'" in package and 'CyanDeck.Launcher' not in package and 'CyanDeck.exe' not in package,
    "legacy updater refuses uncoordinated CyanDeck.exe": "Update ZIP includes a non-application file." in updater and "'DesktopDeck.exe'" in updater and "'CyanDeck.exe'" not in updater,
    "original build unchanged": "src\\DesktopDeck\\DesktopDeck.csproj" in build and "CyanDeck.Launcher" not in build,
    "unsigned preview carries a version-matched warning":
        'src/DesktopDeck/DesktopDeck.csproj' in preview_builder
        and '"-p:Version=$legacyVersion"' in preview_builder
        and 'PREVIEW-README.txt' in preview_builder
        and 'PREVIEW-README.txt' in preview_workflow,
    "legacy roaming/local storage unchanged": "ActiveRoaming => LegacyRoaming" in rootpaths and "ActiveLocal => LegacyLocal" in rootpaths,
}
for label, passed in checks.items():
    print(("PASS" if passed else "FAIL") + ": " + label)
print(f"CyanDeck launcher contract: {sum(checks.values())}/{len(checks)}")
if not all(checks.values()):
    raise SystemExit(1)


# Preserve the separate unsigned portable-pair preview while the signed
# legacy updater intentionally rejects an additional executable.
portable_script = read("tools/build-cyandeck-portable-preview.ps1")
portable_test = read("tools/test_cyandeck_portable_preview.ps1")
portable_checks = {
    "portable preview compiles both entry points":
        "build-cyandeck-launcher-preview.ps1" in portable_script
        and "dotnet publish" in portable_script and "'DesktopDeck.exe'" in portable_script,
    "portable preview refuses OAuth credential embedding":
        "json/client_secret.json" in portable_script and "throw 'Portable preview refuses" in portable_script,
    "portable preview refuses existing output":
        "Portable preview output already exists" in portable_script,
    "portable preview protects installation and profile directories":
        "Programs/DesktopDeck" in portable_script and "'CyanDeck'" in portable_script
        and "ReparsePoint" in portable_script,
    "portable validator checks versions and exact three files":
        "FileVersionInfo" in portable_test and "PREVIEW-README.txt" in portable_test
        and "entries.Count -ne" in portable_test,
    "CI artifact includes both executables and unsigned warning":
        "cyandeck-portable-preview-" in preview_workflow
        and "/DesktopDeck.exe" in preview_workflow
        and "Upload unsigned portable preview" in preview_workflow,
}
for label, passed in portable_checks.items():
    print(("PASS" if passed else "FAIL") + ": " + label)
if not all(portable_checks.values()):
    raise SystemExit(1)


nativeProject = read("src/DesktopDeck/DesktopDeck.csproj")
nativeBuilder = read("tools/build-cyandeck-native-preview.ps1")
nativeVerifier = read("tools/test_cyandeck_native_preview.ps1")
nativeWorkflow = read(".github/workflows/build.yml")
nativeApp = read("src/DesktopDeck/App.xaml.cs")
nativeChecks = {
    "native WPF binary selected explicitly while keeping old release default":
        "CyanDeckNative" in nativeProject and "<AssemblyName>DesktopDeck</AssemblyName>" in nativeProject,
    "native builder publishes the full WPF project retaining the icon":
        "src/DesktopDeck/DesktopDeck.csproj" in nativeBuilder
        and "-p:CyanDeckNative=true" in nativeBuilder
        and "<ApplicationIcon>AppIcon.ico</ApplicationIcon>" in nativeProject,
    "native preview refuses embedded OAuth credentials":
        "json/client_secret.json" in nativeBuilder and "ReparsePoint" in nativeBuilder,
    "native version and safety warnings are verified":
        "FileVersionInfo" in nativeVerifier and "UNPROVISIONED" in nativeVerifier,
    "native app retains writer gate and does not activate staged profile":
        "CYANDECK_NATIVE" in nativeApp
        and "LegacyProfileWriterGate.Acquire()" in nativeApp
        and "CyanDeckCutoverJournal.ThrowIfLegacyWriterFenced()" in nativeApp,
    "native preview does not change signed release artifacts":
        "cyandeck-native-preview-" in nativeWorkflow
        and "-p:CyanDeckNative=true" not in read("package.ps1"),
}
for label, passed in nativeChecks.items():
    print(("PASS" if passed else "FAIL") + ": " + label)
if not all(nativeChecks.values()):
    raise SystemExit(1)
