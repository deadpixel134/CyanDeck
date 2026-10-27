#!/usr/bin/env python3
"""v8.0.32: per-group editor belongs under Appearance navigation, not another root.

v8.0.40 renamed the appearance root to "그룹 모양" and moved the outside-group icon
opacity next to the other desktop icon settings.

v8.0.43 settings redesign: the per-group editor is split into topic cards whose leaves all
sit under "그룹 모양" (never a root of their own), previews/shortcut arrows moved to the
desktop icon root, and the editor became a width-free StackPanel instead of a fixed 560 px grid."""
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
appearance=next(t for t in roots if t.get("Header")=="그룹 모양")
children=appearance.findall(w+"TreeViewItem")
desktop_icons=next(t for t in roots if t.get("Header")=="바탕화면 아이콘·휴지통")
groups=[x for x in children if x.get("Header")=="그룹 개별 편집"]
tabs={x.get(name):x for x in tree.iter(w+"TabItem")}
groups_tab=tabs.get("GroupsTab")
group_controls={x.get(name) for x in groups_tab.iter() if x.get(name)} if groups_tab is not None else set()
all_ids=[x.get(name) for x in tree.iter() if x.get(name)]
def contains(tab, key):
    return key in {node.get(name) for node in tabs[tab].iter()}
group_editor=next((x for x in tree.iter() if x.get(name)=="GroupEditor"), None)
group_leaf_headers={"그룹 개별 편집","그룹별 아이콘","그룹별 배경","그룹별 제목 표시줄","그룹별 접기·잠금","모든 그룹 공통 간격"}
def width_at_most(element, limit):
    width=element.get("Width") if element is not None else None
    if width is None: return element is not None
    try: return float(width) <= limit
    except ValueError: return False
checks={
    "Appearance navigation contains individual group editor as a direct subcategory":
        len(groups)==1 and groups[0].get("Tag")=="GroupsTab|GroupEditorSection",
    "no duplicate top-level per-group editor category":
        not any(x.get("Header")=="그룹 개별 편집" for x in roots),
    "Appearance sidebar still exposes the common Chameleon, title bar, surface and motion options":
        {"흐리게 (카멜레온)","제목 표시줄 버튼","배경 흐림·스크롤바","아이콘 이동 효과"}.issubset(
            {x.get("Header") for x in children}),
    "every per-group editor card is listed under the group appearance root, never as a root":
        group_leaf_headers.issubset({x.get("Header") for x in children if (x.get("Tag") or "").startswith("GroupsTab|")}) and
        not any((x.get("Tag") or "").startswith("GroupsTab|") for x in roots),
    "outside-group icons are listed with the desktop icon settings":
        any(x.get("Header")=="그룹 밖 아이콘" and x.get("Tag")=="SystemIconsTab|StandaloneSection"
            for x in desktop_icons.findall(w+"TreeViewItem")) and
        any(x.get("Tag")=="SystemIconsTab|DesktopIconStyleSection"
            for x in desktop_icons.findall(w+"TreeViewItem")),
    "dedicated group editor page retained for legibility":
        groups_tab is not None and groups_tab.get("Header")=="그룹 개별 편집" and
        "그룹을 선택한 뒤" in ET.tostring(groups_tab,encoding="unicode") and
        any(x.get("CornerRadius")=="10" and x.get("Padding")=="18"
            for x in groups_tab.iter(w+"Border")),
    "group color, text, icon arrangement and rollup editor still exist":
        {"FencePicker","GroupEditor","BackgroundColorBox","TitleColorBox",
         "IconSizeSlider","TitleFontCombo","RollupModeCombo",
         "LinkedIconSpacingCheck","LockedCheck"}.issubset(group_controls),
    "per-group draft controls stay inside GroupEditor; the picker and global spacing stay outside":
        group_editor is not None and
        {"IconLayoutCombo","IconLabelModeCombo","IconLabelFontSizeSlider","IconSizeSlider",
         "SpacingXSlider","SpacingYSlider","BackgroundColorBox","BackgroundHueSlider",
         "BackgroundSaturationSlider","BackgroundBrightnessSlider","BackgroundTransparencySlider",
         "TitlebarModeCombo","TitleFontCombo","TitleFontSizeSlider","TitleColorBox",
         "RollupModeCombo","HoverDelaySlider","LockedCheck","TitlebarBottomNearTopCheck"}.issubset(
            {x.get(name) for x in group_editor.iter() if x.get(name)}) and
        not ({"FencePicker","LinkedIconSpacingCheck"} & {x.get(name) for x in group_editor.iter() if x.get(name)}),
    "group-specific controls not duplicated on shared appearance page":
        not contains("AppearanceTab","GroupEditor") and
        not contains("SystemIconsTab","GroupEditor") and
        contains("SystemIconsTab","StandaloneIconIdleOpacitySlider"),
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
        width_at_most(group_editor, 560),
    "save and close remain in the unified transaction footer":
        all('Click="'+action+'"' in xaml for action in
            ["Apply_Click","Cancel_Click"]),
    "v8.0.32 appearance navigation is data-migration free":
        tuple(map(int,re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", project).group(1).split("."))) >= (8,0,32) and
        "Current = 35" in read("src/CyanDeck/Services/StateSchema.cs"),
}
for k,v in checks.items():print(("PASS" if v else "FAIL")+": "+k)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()):raise SystemExit(1)
