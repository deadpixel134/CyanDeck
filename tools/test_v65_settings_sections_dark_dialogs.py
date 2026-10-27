#!/usr/bin/env python3
"""v8.0.42: every Settings sidebar entry opens its own card titled like the entry and scrolls
that card into full view; the blank-desktop group creation settings have their own section;
rule editor, tray/desktop menus, calendar buttons, dialogs and the date picker use the dark theme.
Non-destructive static checks; visual verification happens on Windows.

v8.0.43: the sidebar grew to 46 leaves (one per section card); the leaf count is now derived
from the section cards instead of fixed, with every per-leaf rule unchanged.

v8.0.44: "대량 작업 확인" and "복구 지점" are new leaves with their own cards (48 leaves);
the per-leaf rules apply to them unchanged."""
from pathlib import Path
from xml.etree import ElementTree as ET
import re

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

W = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
X = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"

settings_xaml = read("src/CyanDeck/Views/SettingsWindow.xaml")
settings = read("src/CyanDeck/Views/SettingsWindow.xaml.cs")
rule_xaml = read("src/CyanDeck/Views/RuleEditorWindow.xaml")
rule = read("src/CyanDeck/Views/RuleEditorWindow.xaml.cs")
tray = read("src/CyanDeck/Services/TrayIconController.cs")
app = read("src/CyanDeck/Services/AppController.cs")
renderer = read("src/CyanDeck/Services/DeckToolStripRenderer.cs")
calendar = read("src/CyanDeck/Calendar/CalendarWindow.cs")
time_picker = read("src/CyanDeck/Calendar/CalendarTimePicker.cs")
color_picker = read("src/CyanDeck/Calendar/CalendarColorPicker.cs")
prompt = read("src/CyanDeck/Views/TextPrompt.cs")
wizard = read("src/CyanDeck/Views/FirstRunWizardWindow.cs")
delete_dialog = read("src/CyanDeck/Views/CollectionDesktopDeleteDialog.cs")
startup = read("src/CyanDeck/Views/StartupUpdateWindow.cs")
gallery_xaml = read("src/CyanDeck/Views/SnapshotGalleryWindow.xaml")
gallery = read("src/CyanDeck/Views/SnapshotGalleryWindow.xaml.cs")
dark_text = read("src/CyanDeck/Themes/DarkControls.xaml")
project = read("src/CyanDeck/CyanDeck.csproj")

tree = ET.fromstring(settings_xaml)
parents = {child: parent for parent in tree.iter() for child in parent}
named = {node.get(X): node for node in tree.iter() if node.get(X)}
tabs = {node.get(X): node for node in tree.iter(W + "TabItem")}
leaves = [item for item in tree.iter(W + "TreeViewItem") if item.find(W + "TreeViewItem") is None]
anchors = {leaf.get("Tag").split("|", 1)[1] for leaf in leaves}
# v8.0.43: every section card title (SectionTitle TextBlock) must be reachable from exactly one
# leaf, so the leaf-to-card bijection covers all cards instead of a fixed count.
section_titles = [n for n in tree.iter(W + "TextBlock") if (n.get("Style") or "") == "{StaticResource SectionTitle}"]

section_issues = []
for leaf in leaves:
    header = leaf.get("Header")
    anchor = leaf.get("Tag").split("|", 1)[1]
    node = named.get(anchor)
    if node is None:
        section_issues.append((header, "anchor missing")); continue
    panel = parents.get(node)
    card = parents.get(panel) if panel is not None else None
    if node.tag != W + "TextBlock" or "SectionTitle" not in (node.get("Style") or ""):
        section_issues.append((header, "not a section title"))
    if card is None or card.tag != W + "Border" or card.get("CornerRadius") != "10":
        section_issues.append((header, "not inside its own card")); continue
    if list(panel)[0] is not node:
        section_issues.append((header, "title is not the first line of the card"))
    others = [n.get(X) for n in card.iter() if n.get(X) in anchors and n.get(X) != anchor]
    if others:
        section_issues.append((header, "card also holds " + ",".join(others)))
    if node.get("Text") != header:
        section_issues.append((header, f"title '{node.get('Text')}' differs from the sidebar"))
    if node.get("Margin"):
        section_issues.append((header, "title spacing differs from other cards"))

def in_tab(tab, name):
    return any(n.get(X) == name for n in tabs[tab].iter())

create_card = parents[parents[named["CreateGroupSection"]]]
create_names = {n.get(X) for n in create_card.iter() if n.get(X)}
create_text = ET.tostring(create_card, encoding="unicode")

dark = ET.fromstring(dark_text)
dark_named = {n.get(X): n for n in dark.iter() if n.get(X)}
def grid_size(name):
    grid = dark_named[name]
    rows = grid.find(W + "Grid.RowDefinitions")
    cols = grid.find(W + "Grid.ColumnDefinitions")
    return (len(list(rows)) if rows is not None else 0, len(list(cols)) if cols is not None else 0)

version = tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", project).group(1).split(".")))

checks = {
    "every sidebar entry opens its own card titled exactly like the entry":
        len(leaves) == len(section_titles) and len(anchors) == len(leaves) and
        {n.get(X) for n in section_titles} == anchors and not section_issues,
    "blank-desktop group creation has its own section with the button choice and its limits":
        {"CreateGroupSection", "CreateGroupMouseButtonCombo"} <= create_names and
        "사각형을 그릴 마우스 버튼" in create_text and "범위 선택" in create_text and
        "관리 정책" in create_text and "UnhideOnPageSwitchCheck" not in create_names,
    # v8.0.44: the batch confirmation covers opening group items too, so it left the folder
    # portal card; recovery points are listed in Settings and restore like an import.
    "large-batch confirmation and recovery points have their own cards and searchable entries":
        {"BatchConfirmSection", "RecoveryPointsSection"} <= anchors and
        in_tab("InteractionTab", "BatchThresholdSlider") and in_tab("LayoutTab", "RecoveryPointList") and
        '["BatchConfirmSection"]' in settings and '["RecoveryPointsSection"]' in settings and
        "var canRestore = !_compatibilityReadOnly" in settings and
        "if (restore(path)) CloseWithoutPrompt();" in settings,
    "group snapping is its own section and the quick-hide option lives on the quick-hide page":
        in_tab("InteractionTab", "SnapSection") and in_tab("PeekTab", "UnhideOnPageSwitchCheck") and
        not in_tab("InteractionTab", "UnhideOnPageSwitchCheck") and
        'Tag="InteractionTab|SnapSection"' in settings_xaml and '["SnapSection"]' in settings,
    "choosing an entry scrolls its whole card to the top; re-clicking it scrolls again":
        "ScrollSectionToTop(target, keepContextAbove: hit is not null);" in settings and
        "BringIntoView(new Rect(0, -24" not in settings and
        "ScrollToVerticalOffset(" in settings and "HookRepeatNavigationClicks();" in settings,
    "rule editor uses one save point and asks before closing with unsaved rules":
        'Content="저장" Click="Apply_Click" Style="{StaticResource DeckPrimaryButtonStyle}"' in rule_xaml and
        'Content="닫기" IsCancel="True" Click="Cancel_Click"' in rule_xaml and
        "Ok_Click" not in rule_xaml and "Ok_Click" not in rule and
        "MessageBoxButton.YesNoCancel" in rule and "SC_CLOSE" in rule and
        "private bool HasUnsavedChanges()" in rule and "Key.S" in rule,
    "rule editor is dark (shared controls, dark column headers)":
        "../Themes/DarkControls.xaml" in rule_xaml and 'TargetType="DataGridColumnHeader"' in rule_xaml and
        'BasedOn="{StaticResource DarkButtonStyle}"' in rule_xaml,
    "tray and blank-desktop menus use the dark renderer, high contrast excluded":
        "DeckToolStripRenderer.Apply(_menu)" in tray and "DeckToolStripRenderer.Apply(menu)" in app and
        "SystemInformation.HighContrast" in renderer and "OnRenderItemCheck" in renderer and
        "Tray menu theme failed" in tray,
    "calendar buttons, time picker and colour swatches keep dark hover and disabled states":
        '"DarkButtonStyle"' in calendar.split("private static Button MakeButton(string label)", 1)[1].split("\n    }", 1)[0] and
        '"DarkButtonStyle"' in time_picker and '"DeckPrimaryButtonStyle"' in time_picker and
        '"DeckSwatchButtonStyle"' in color_picker and 'x:Key="DeckSwatchButtonStyle"' in dark_text and
        'MakePrimaryButton("저장 (Ctrl+Enter)")' in calendar and '"DeckDangerButtonStyle"' in calendar,
    "small dialogs use dark buttons with primary and danger roles":
        "DeckThemeResources.MergeDarkControls(window.Resources)" in prompt and
        "DeckThemeResources.MergeDarkControls(Resources)" in wizard and
        "DeckThemeResources.MergeDarkControls(Resources)" in delete_dialog and "recycle.Style = danger;" in delete_dialog and
        '"DarkButtonStyle"' in startup and "../Themes/DarkControls.xaml" in gallery_xaml and
        '"DeckDangerButtonStyle"' in gallery,
    "dark date picker keeps the parts DatePicker, Calendar and CalendarItem fill in":
        all(f'x:Name="{p}"' in dark_text for p in ("PART_TextBox", "PART_Button", "PART_Popup", "PART_Root",
            "PART_CalendarItem", "PART_PreviousButton", "PART_HeaderButton", "PART_NextButton",
            "PART_MonthView", "PART_YearView", "PART_DisabledVisual", "PART_Watermark", "PART_ContentHost")) and
        "{x:Static CalendarItem.DayTitleTemplateResourceKey}" in dark_text and
        grid_size("PART_MonthView") == (7, 7) and grid_size("PART_YearView") == (3, 4),
    "event editor date fields use the dark date picker":
        '"DeckDatePickerStyle"' in calendar and "startPicker.Style = datePickerStyle;" in calendar,
    "sections, menus and dialogs beta version or later":
        version >= (8, 0, 42),
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
for header, problem in section_issues: print("SECTION:", header, "-", problem)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
