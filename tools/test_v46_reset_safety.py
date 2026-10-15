#!/usr/bin/env python3
"""Non-destructive reset safety guard. Static checks only; never launches WPF or changes user data."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
def read(path):
    return (root / path).read_text(encoding="utf-8-sig")

controller = read("src/CyanDeck/Services/AppController.cs")
xaml = read("src/CyanDeck/Views/SettingsWindow.xaml")
ui = read("src/CyanDeck/Views/SettingsWindow.xaml.cs")
policy = read("src/CyanDeck/Layout/InitialGeometryResetPolicy.cs")
src = ET.fromstring(xaml)
def section(begin, end):
    return controller.split(begin, 1)[1].split(end, 1)[0]

geometry = section("private bool ResetVisualGeometryToDefaults(", "private bool ResetCyanDeckToDefaults()")
factory = section("private bool ResetCyanDeckToDefaults()", "private void ReplaceState(")
guard = section("private bool ResetAllWindowGeometryToDefaults()", "private bool ResetCyanDeckToDefaults()")
names = {e.attrib.get("{http://schemas.microsoft.com/winfx/2006/xaml}Name"): e for e in src.iter()}
explanation = next(e for e in src.iter()
                   if e.tag.endswith("TextBlock") and
                   e.attrib.get("Text", "").startswith("삭제된 기본 그룹이나 빠진 아이콘까지"))
checks = {
    "program reset XAML description has no stray property text":
        not explanation.tail or not explanation.tail.strip(),
    "three reset UI commands remain independently accessible":
        all(name in names for name in (
            "ResetAllWindowGeometryButton", "ResetPlacementButton", "FactoryResetButton")),
    "three reset buttons use separate click handlers":
        all(name in xaml and ("private void " + name) in ui for name in (
            "ResetAllWindowGeometry_Click(", "ResetPlacement_Click(", "FactoryReset_Click(")),
    "all-window reset never rebuilds group contents":
        "ResetVisualGeometryToDefaults(groups: true, calendar: true, resetGroupContent: false)" in guard,
    "group default recovery remains separate and explicitly destructive to group configuration":
        "if (groups && resetGroupContent)" in geometry and "CreateDefaultLayout();" in geometry,
    "geometry reset keeps group identifiers and contents unchanged":
        "if (groups && !resetGroupContent)" in geometry and
        "InitialGeometryResetPolicy.Plan(" in geometry and
        "f.X = placement.X" in geometry and "f.Width = placement.Width" in geometry,
    "calendar reset only touches window geometry":
        all(("Settings." + name) in geometry for name in (
            "CalendarLeft", "CalendarTop", "CalendarWidth", "CalendarHeight")) and
        "CalendarDefaults" in geometry,
    "geometry reset requires read-back of backup before mutation":
        geometry.index("_store.Import(backup);") < geometry.index("if (groups && !resetGroupContent)"),
    "geometry reset verifies nonempty exported backup and recovery checkpoint":
        'new FileInfo(backup).Length == 0' in geometry and
        'new FileInfo(checkpoint).Length == 0' in geometry and
        'before-geometry-reset' in geometry,
    "factory reset requires read-back of backup before mutation":
        factory.index("_store.Import(backup);") < factory.index("ReplaceState(defaults);"),
    "factory reset checks both backup and recovery checkpoint":
        'new FileInfo(backup).Length == 0' in factory and
        'new FileInfo(checkpoint).Length == 0' in factory and
        'before-factory-reset' in factory,
    "backup failures never call factory state replacement":
        "if (resetStarted && backup is not null && File.Exists(backup))" in factory and
        factory.index("resetStarted = true;") > factory.index("_store.Import(backup);"),
    "factory rollback persists restored data":
        'ReplaceState(_store.Import(backup));' in factory and
        factory.split('ReplaceState(_store.Import(backup));', 1)[1].split('catch (Exception recoveryEx)', 1)[0].count('requireDurableSave: true') == 1,
    "factory confirmation remains two step":
        "MessageBoxButton.YesNo" in factory and "'초기화'" in factory,
    "both reset paths fail closed under protected display/managed modes":
        "_safeMode" in geometry and "_compatibilityReadOnly" in geometry and
        "_safeMode" in factory and "_compatibilityReadOnly" in factory,
    "OAuth public client configuration preserved":
        "defaults.Settings.GoogleCalendarClientId = _state.Settings.GoogleCalendarClientId;" in factory,
    "both reset operations leave user files/tokens/events untouched":
        all(not any(x in method for x in (
            "File.Delete(", "Directory.Delete(", "calendar-events.json", "calendar-google-token.dat"))
            for method in (geometry, factory)),
    "existing geometry layout helper and original calendar dimensions retained":
        "MonitorGroupRecoveryPlanner.Plan(" in policy and
        "(120, 110, 650, 510)" in policy,
    "no schema migration required for reset safety only":
        "Current = 35" in read("src/CyanDeck/Services/StateSchema.cs"),
}
for label, passed in checks.items():
    print(("PASS" if passed else "FAIL") + ": " + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()):
    raise SystemExit(1)
