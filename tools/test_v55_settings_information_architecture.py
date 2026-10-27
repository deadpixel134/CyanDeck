#!/usr/bin/env python3
"""v8.0.31: non-destructive settings navigation and spacing regression guard.

v8.0.40 regrouped the sidebar by task (Peek/quick hide, desktop icons, updates and
troubleshooting got their own entries); the safety intent of every check is kept."""
import re
from collections import Counter
from pathlib import Path
from xml.etree import ElementTree as ET

root = Path(__file__).resolve().parents[1]
xaml = (root / "src/CyanDeck/Views/SettingsWindow.xaml").read_text(encoding="utf-8-sig")
code = (root / "src/CyanDeck/Views/SettingsWindow.xaml.cs").read_text(encoding="utf-8-sig")
project = (root / "src/CyanDeck/CyanDeck.csproj").read_text(encoding="utf-8-sig")
xmlns = {"w": "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
         "x": "http://schemas.microsoft.com/winfx/2006/xaml"}
ns = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"
tree = ET.fromstring(xaml)
tabs = {node.get(ns): node for node in tree.findall(".//w:TabItem", xmlns)}
named = [node.get(ns) for node in tree.iter() if node.get(ns)]
names = Counter(named)
routes = [(leaf.get("Tag"), leaf.get("Header"))
          for leaf in tree.findall(".//w:TreeViewItem", xmlns)]
route_issues = []
for route, label in routes:
    if not route or "|" not in route:
        route_issues.append((label, route, "missing route"))
        continue
    tab_id, anchor_id = route.split("|", 1)
    if tab_id not in tabs:
        route_issues.append((label, route, "tab missing"))
    elif not any(child.get(ns) == anchor_id for child in tabs[tab_id].iter()):
        route_issues.append((label, route, "target in wrong tab"))
    if f'"{tab_id}" => {tab_id}' not in code:
        route_issues.append((label, route, "code-behind route missing"))

def has(tab, *ids):
    present = {node.get(ns) for node in tabs[tab].iter()}
    return all(x in present for x in ids)

def cards(tab):
    return [n for n in tabs[tab].iter()
            if n.tag.endswith("}Border") and n.get("CornerRadius") == "10"
            and n.get("Padding") == "18"]

new_tabs = ["GeneralTab", "InteractionTab", "AppearanceTab",
            "AnimationTab", "PerformanceTab", "LayoutTab", "PeekTab"]
checks = {
    "settings XAML parses with unique named controls":
        len(names) >= 216 and all(count == 1 for count in names.values()),
    "all 44 navigation roots/leaves resolve to their actual owning page":
        len(routes) == 44 and not route_issues,
    "original calendar/system/group/about editors remain as distinct tabs":
        all(x in tabs for x in ("CalendarTab", "SystemIconsTab", "GroupsTab", "AboutTab")),
    "dedicated desktop settings pages including Peek and quick hide":
        all(x in tabs for x in new_tabs),
    "general page now contains only feature and base desktop controls":
        has("GeneralTab", "FeaturesSection", "DesktopSection",
            "StartupCheck", "DefaultDesktopPlacementCombo")
        and not has("GeneralTab", "AnimatePagesCheck")
        and not has("GeneralTab", "ChameleonCheck"),
    "interaction page collects touch, snapping, tabs and rollup":
        has("InteractionTab", "TouchPageGesturesCheck", "SmartSnapCheck",
            "TabsSection", "RollupSection"),
    "Peek and quick hide have their own page":
        has("PeekTab", "PeekSection", "QuickHideSection", "PeekWorkAreaCheck") and
        not has("InteractionTab", "PeekSection") and not has("InteractionTab", "QuickHideSection"),
    "appearance groups icon previews, header controls and chameleon":
        has("AppearanceTab", "DesktopIconStyleSection", "ShowFilePreviewsCheck",
            "ShowHeaderControlsCheck", "ChameleonSection"),
    "icons outside groups are configured with the other desktop icon settings":
        has("SystemIconsTab", "StandaloneSection", "StandaloneIconHoverOpacitySlider",
            "StandaloneIconIdleOpacitySlider") and not has("AppearanceTab", "StandaloneSection"),
    "standalone appearance moved out of per-group editor without losing group fields":
        not has("GroupsTab", "StandaloneSection") and
        has("GroupsTab", "GroupEditorSection", "GroupEditor", "BackgroundColorBox",
            "TitleColorBox", "IconSizeSlider"),
    "standalone icon appearance retains Settings.EditGroups managed policy":
        has("SystemIconsTab", "StandaloneAppearanceCard") and
        "StandaloneAppearanceCard.IsEnabled = GroupsTab.IsEnabled;" in code,
    "all page and icon motion settings consolidated":
        has("AnimationTab", "PageAnimationSection", "AnimatePagesCheck",
            "EnhancedPageEasingCheck", "PageDurationSlider", "ItemAnimationSection",
            "AnimateItemChangesCheck", "AnimateIconsWhenMovedCheck",
            "IconMovementDurationSlider"),
    "performance page owns deferred and background loading":
        has("PerformanceTab", "PerformanceSection", "SaveDebounceSlider",
            "LoadingPerformanceSection", "AsyncItemLoadingCheck", "IconCacheSlider"),
    "layout and backup page owns snapshots and import/export":
        has("LayoutTab", "SnapshotsSection", "LayoutAdjustmentCombo",
            "BackupSection", "ImportConfigurationButton"),
    "settings default restore sits with the other resets and keeps the general-settings policy":
        has("AboutTab", "ResetSection", "ResetGeneralButton", "FactoryResetSection") and
        "ResetGeneralButton.IsEnabled = GeneralTab.IsEnabled;" in code,
    "updates and version information have their own page":
        has("UpdatesTab", "UpdatesSection", "AutomaticUpdateCheck", "BetaUpdateChannelCheck",
            "AboutSection", "AboutVersionText") and len(cards("UpdatesTab")) >= 2,
    "all desktop pages have spaced card sections":
        all(len(cards(k)) >= 2 for k in new_tabs) and
        all("Margin=\"28,24,26,38\"" in ET.tostring(tabs[k], encoding="unicode")
            for k in new_tabs),
    "larger window and wider sidebar":
        tree.get("Width") == "1040" and tree.get("Height") == "760"
        and "Width=\"222\"" in xaml,
    "new tabs inherit managed policy read-only behavior":
        all(f"{x}.IsEnabled = GeneralTab.IsEnabled;" in code for x in new_tabs[1:]),
    "settings search continues routing to expanded category leaves":
        "SettingsNavigationLeaves(SettingsNavigation)" in code and
        "first.IsSelected = true;" in code and
        all(x in code for x in ("PageAnimationSection", "DesktopIconStyleSection",
                               "ItemAnimationSection", "LoadingPerformanceSection")),
    "settings search also matches the wording inside each section":
        "BuildSearchIndex()" in code and "CollectSearchText(" in code and
        'KeyDown="SettingsSearchBox_KeyDown"' in xaml and
        "private void SettingsSearchBox_KeyDown(" in code,
    # v8.0.41: one explicit save point ("저장") and "닫기" replace 적용/확인/취소.
    "save and close transaction controls remain intact":
        all(f'Click="{name}"' in xaml for name in ("Apply_Click", "Cancel_Click")) and
        'Click="Ok_Click"' not in xaml,
    "no new state migration":
        tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", project).group(1).split("."))) >= (8,0,31)
        and "Current = 35" in
        (root / "src/CyanDeck/Services/StateSchema.cs").read_text(encoding="utf-8-sig")
}
for name, value in checks.items():
    print(("PASS: " if value else "FAIL: ") + name)
for label,route,error in route_issues:
    print("BAD ROUTE:",label,route,error)
print("RESULT",sum(checks.values()),"/",len(checks))
if not all(checks.values()):
    raise SystemExit(1)
