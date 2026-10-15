#!/usr/bin/env python3
"""v8.0.32: per-group editor belongs under Appearance navigation, not another root."""
from pathlib import Path
from xml.etree import ElementTree as ET
from collections import Counter
import re

root=Path(__file__).resolve().parents[1]
def read(path):return (root/path).read_text(encoding="utf-8-sig")
xaml=read("src/CyanDeck/Views/SettingsWindow.xaml")
code=read("src/CyanDeck/Views/SettingsWindow.xaml.cs")
project=read("src/CyanDeck/CyanDeck.csproj")
tree=ET.fromstring(xaml)
w="{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
name="{http://schemas.microsoft.com/winfx/2006/xaml}Name"
nav=next(t for t in tree.iter(w+"TreeView") if t.get(name)=="SettingsNavigation")
roots=list(nav.findall(w+"TreeViewItem"))
appearance=next(t for t in roots if t.get("Header")=="모양 및 외관")
children=appearance.findall(w+"TreeViewItem")
groups=[x for x in children if x.get("Header")=="그룹 개별 편집"]
tabs={x.get(name):x for x in tree.iter(w+"TabItem")}
groups_tab=tabs.get("GroupsTab")
group_controls={x.get(name) for x in groups_tab.iter() if x.get(name)} if groups_tab is not None else set()
all_ids=[x.get(name) for x in tree.iter() if x.get(name)]
def contains(tab, key):
    return key in {node.get(name) for node in tabs[tab].iter()}
checks={
    "Appearance navigation contains individual group editor as a direct subcategory":
        len(groups)==1 and groups[0].get("Tag")=="GroupsTab|GroupEditorSection",
    "no duplicate top-level per-group editor category":
        not any(x.get("Header")=="그룹 개별 편집" for x in roots),
    "Appearance sidebar still exposes common icon and Chameleon options":
        {"아이콘 표시 방식","그룹 제목줄·스크롤바","카멜레온·투명도","단독 아이콘 외관"}.issubset(
            {x.get("Header") for x in children}),
    "dedicated group editor page retained for legibility":
        groups_tab is not None and groups_tab.get("Header")=="그룹 개별 편집" and
        "그룹을 선택한 뒤" in ET.tostring(groups_tab,encoding="unicode") and
        any(x.get("CornerRadius")=="10" and x.get("Padding")=="18"
            for x in groups_tab.iter(w+"Border")),
    "group color, text, icon arrangement and rollup editor still exist":
        {"FencePicker","GroupEditor","BackgroundColorBox","TitleColorBox",
         "IconSizeSlider","TitleFontCombo","RollupModeCombo",
         "LinkedIconSpacingCheck","LockedCheck"}.issubset(group_controls),
    "group-specific controls not duplicated on shared appearance page":
        not contains("AppearanceTab","GroupEditor") and
        contains("AppearanceTab","StandaloneIconIdleOpacitySlider"),
    "separate Settings.EditGroups capability continues to guard the group editor":
        'GroupsTab.IsEnabled = _isManagedCapabilityAllowed("Settings.EditGroups")' in code and
        '"GroupsTab" => GroupsTab' in code,
    "standalone appearance card still obeys group permission":
        'StandaloneAppearanceCard.IsEnabled = GroupsTab.IsEnabled' in code,
    "all settings input fields retain unique identities":
        len(all_ids) >= 216 and all(n==1 for n in Counter(all_ids).values()),
    "new direct subcategory is reachable using existing search":
        'SettingsNavigationLeaves(SettingsNavigation)' in code and
        '"GroupEditorSection"' in code and
        'first.IsSelected = true' in code,
    "all navigation routes point to existing tab and named target":
        all(
            (lambda a: a[0] in tabs and contains(a[0],a[1]))
            (item.get("Tag").split("|",1))
            for item in nav.iter(w+"TreeViewItem")),
    "group editor stays fully visible at minimum settings window width":
        int(tree.get("MinWidth","0")) >= 940 and
        'Width="560"' in xaml,
    "apply, confirm and cancel remain in unified transaction footer":
        all('Click="'+action+'"' in xaml for action in
            ["Apply_Click","Ok_Click","Cancel_Click"]),
    "v8.0.32 appearance navigation is data-migration free":
        "<Version>8.0.32</Version>" in project and
        "Current = 35" in read("src/CyanDeck/Services/StateSchema.cs"),
}
for k,v in checks.items():print(("PASS" if v else "FAIL")+": "+k)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()):raise SystemExit(1)
