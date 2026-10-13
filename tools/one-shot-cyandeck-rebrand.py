#!/usr/bin/env python3
"""One-time clean CyanDeck source rename. No access to user profiles."""
from pathlib import Path
import subprocess
import re

ROOT=Path(__file__).resolve().parents[1]
def rename(s):
    return s.replace("DESKTOPDECK","CYANDECK").replace("DesktopDeck","CyanDeck").replace("desktopdeck","cyandeck")

REMOVE_PREFIX=(
    "src/CyanDeck.Launcher/",
    "tests/CyanDeckLauncherFixture/",
    "tests/CyanDeckLauncherHarness/",
)
REMOVE_NAMES={
    "src/CyanDeck/Services/CyanDeckCutoverJournal.cs",
    "src/CyanDeck/Services/LegacyProfileWriterGate.cs",
    "docs/CYANDECK_MIGRATION.md",
    "docs/CYANDECK_PHASE15_WINDOWS_SMOKE_KO.md",
    ".github/workflows/one-time-cyandeck-rebrand.yml",
    "tools/one-shot-cyandeck-rebrand.py",
}
TEXT_EXT={".cs",".csproj",".xaml",".ps1",".py",".json",".yml",".yaml",".md",
          ".txt",".cmd",".manifest",".xml"}
allfiles=subprocess.check_output(["git","ls-files","-z"],cwd=ROOT).decode().strip("\0").split("\0")
plans=[]
for oldpath in filter(None,allfiles):
    file=ROOT/oldpath
    if not file.is_file(): continue
    newpath=rename(oldpath)
    if oldpath=="src/DesktopDeck/Services/LegacySingleInstanceGate.cs":
        newpath="src/CyanDeck/Services/CyanDeckSingleInstanceGate.cs"
    excluded=(oldpath.startswith(REMOVE_PREFIX) or newpath in REMOVE_NAMES or
       oldpath in REMOVE_NAMES or
       (oldpath.startswith("tools/") and "cyandeck" in oldpath.lower() and
         oldpath not in {"tools/test_cyandeck_update_package_layout.ps1"}))
    if excluded:
        plans.append((file,None,None))
        continue
    data=file.read_bytes()
    if file.suffix.lower() in TEXT_EXT or file.name==".gitignore":
        content=rename(data.decode("utf-8-sig"))
        content=content.replace("LegacySingleInstanceGate","CyanDeckSingleInstanceGate")
        content=content.replace("deadpixel134/CyanDeck","deadpixel134/DesktopDeck")
        if oldpath=="src/DesktopDeck/DesktopDeck.csproj":
            content=re.sub(r'(?m)^\s*<AssemblyName Condition=.*\n','',content)
            content=re.sub(r'(?m)^\s*<DefineConstants Condition=.*\n','',content)
            content=content.replace("<Version>7.49.6</Version>","<Version>8.0.0</Version>")
        if oldpath=="src/DesktopDeck/App.xaml.cs":
            begin=content.index("        // Reject unsupported cutover requests")
            end=content.index("        WaitForPriorInstanceIfRequested",begin)
            content=content[:begin]+content[end:]
            content=re.sub(r'(?m)^\s*private LegacyProfileWriterGate\? _legacyWriterGate;\n','',content)
            begin=content.index("        // Both mutexes stay owned")
            end=content.index("        _controller = new AppController",begin)
            content=content[:begin]+content[end:]
            begin=content.index("#if CYANDECK_NATIVE")
            end=content.index("#endif",begin)+len("#endif")
            content=content[:begin]+'        DiagnosticsLog.Info("CyanDeck starting");'+content[end:]
            begin=content.index("    protected override void OnExit(ExitEventArgs e)")
            content=content[:begin]+"""    protected override void OnExit(ExitEventArgs e)
    {
        try { _activationCts?.Cancel(); } catch { }
        try { _controller?.Dispose(); }
        finally
        {
            _activationCts?.Dispose();
            _instanceGate?.Dispose();
            base.OnExit(e);
        }
    }
}
"""
        if oldpath=="src/DesktopDeck/Services/DataRootPaths.cs":
            content="""namespace CyanDeck.Services;

// Standalone profile: old DesktopDeck directories are never read or modified.
public static class DataRootPaths
{
    public static string ActiveRoaming => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck");
    public static string ActiveLocal => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyanDeck");
}
"""
        if oldpath=="tools/run_v43_safety_regressions.py":
            content=content.replace("  'test_cyandeck_brand_phase1.py',\n","")
            content=content.replace("  'test_cyandeck_launcher_contract.py',\n","")
        if oldpath=="update-manifest.example.json":
            content=content.replace("7.49.6","8.0.0")
        data=content.encode("utf-8")
    plans.append((file,ROOT/newpath,data))

for original,_,_ in plans: original.unlink()
for _,dest,data in plans:
    if dest is not None:
        dest.parent.mkdir(parents=True,exist_ok=True)
        dest.write_bytes(data)

# Existing updater already uses allowlisted file replacement, signed
# manifests, recovery journal and rollback; keep those tested mechanisms.
updater=ROOT/"apply-update.ps1"
s=updater.read_text(encoding="utf8")
begin=s.find("function Assert-LegacyWriterFenceAbsent")
if begin>=0:
    end=s.index("# Wait until the requesting application",begin)
    s=s[:begin]+s[end:]
s=s.replace("  Assert-LegacyWriterFenceAbsent\n","")
# Single CyanDeck mutex: old cross-executable writer lease is not needed.
s=s.replace("$writerUpdateMutex = [Threading.Mutex]::new($false, 'Local\\CyanDeck.LegacyProfileWriter.v1')\n","")
s=s.replace("$ownsWriterUpdateMutex = $false\n","")
begin=s.find("  try { $ownsWriterUpdateMutex = $writerUpdateMutex.WaitOne(0) }")
if begin>=0:
    end=s.index("# Manual recovery",begin)
    s=s[:begin]+s[end:]
s=s.replace("  if ($ownsWriterUpdateMutex) { $writerUpdateMutex.ReleaseMutex() }\n","")
s=s.replace("  $writerUpdateMutex.Dispose()\n","")
updater.write_text(s,encoding="utf8")

# Installer and uninstaller are single-product only and intentionally do
# not copy/migrate or erase any old product profile or installation.
installer=ROOT/"install.ps1"
s=installer.read_text(encoding="utf8")
s=re.sub(r"(?s)function Assert-LegacyWriterFenceAbsent \{.*?\n\}\n\n", "", s)
s=s.replace("  Assert-LegacyWriterFenceAbsent\n","")
# Strip the second identical shortcut introduced for migration.
s=s.replace('$cyanShortcut = Join-Path $startMenuDir "CyanDeck.lnk"\n',"")
s=s.replace("Assert-NoReparseAncestors $cyanShortcut\n","")
s=s.replace("Assert-ShortcutBelongsToDesktopDeck $cyanShortcut $expectedExe\n","")
s=re.sub(r"(?s)# Preserve the legacy shortcut for existing pins.*?\$cyanLink.Save\(\)\n\n","",s)
s=s.replace(" (legacy: $shortcut)","")
installer.write_text(s,encoding="utf8")

uninstaller=ROOT/"uninstall.ps1"
s=uninstaller.read_text(encoding="utf8")
s=re.sub(r"(?s)function Assert-LegacyWriterFenceAbsent \{.*?\n\}\n\n","",s)
s=s.replace("  Assert-LegacyWriterFenceAbsent\n","")
s=re.sub(r"(?s)if \(\$PurgeData -and \(Test-Path -LiteralPath \(Join-Path \$env:APPDATA 'CyanDeck'\)\)\) \{.*?\n\}\n","",s)
s=s.replace("$cyanStartMenuShortcut = Join-Path $env:APPDATA 'Microsoft\\Windows\\Start Menu\\Programs\\CyanDeck.lnk'\n","")
s=s.replace("@($startMenuShortcut, $cyanStartMenuShortcut, $desktopShortcut)","@($startMenuShortcut, $desktopShortcut)")
uninstaller.write_text(s,encoding="utf8")

# Core deployment files and signed update names are already globally renamed.
# Remove obsolete managed policy example iff still a backward-compat fixture.
if not (ROOT/"src/CyanDeck/AppIcon.ico").is_file(): raise RuntimeError("Original icon missing")
if not (ROOT/"src/CyanDeck/CyanDeck.csproj").is_file(): raise RuntimeError("New project missing")
for d in sorted((p for p in ROOT.rglob("*") if p.is_dir() and ".git" not in p.parts),
                key=lambda p:len(p.parts),reverse=True):
    try:d.rmdir()
    except OSError:pass
if (ROOT/"src/DesktopDeck").exists(): raise RuntimeError("Old source tree still exists after cleanup")
print("One-shot CyanDeck standalone source rewrite complete.")
