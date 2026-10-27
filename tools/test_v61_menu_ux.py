#!/usr/bin/env python3
"""v8.0.38: protect the menu usability cleanup (group menu, tray calendar menu,
calendar settings panel and the remaining Settings pages). Non-destructive static checks."""
from pathlib import Path
import re
from xml.etree import ElementTree as ET

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

menu = read("src/CyanDeck/Views/FenceWindow.ContextMenu.cs")
app = read("src/CyanDeck/Services/AppController.cs")
routing = read("src/CyanDeck/Services/TrayMenuRoutingPolicy.cs")
harness = read("tests/FenceVisibilityHarness/Program.cs")
calendar = read("src/CyanDeck/Calendar/CalendarWindow.cs")
xaml = read("src/CyanDeck/Views/SettingsWindow.xaml")
project = read("src/CyanDeck/CyanDeck.csproj")

populate = menu.split("private void PopulateGroupContextMenu(ContextMenu menu)", 1)[1].split("private MenuItem BuildTabsMenu(", 1)[0]
confirm_delete = menu.split("private void ConfirmDeleteGroup()", 1)[1].split("private void ConfirmClearTabRules()", 1)[0]
prefs = calendar.split("private void TogglePreferences()", 1)[1].split("private void AddPreferencesSectionTitle(", 1)[0]

w = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
x_name = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"
tree = ET.fromstring(xaml)
tabs = {t.get(x_name): t for t in tree.iter(w + "TabItem")}
def cards(tab):
    return [n for n in tabs[tab].iter(w + "Border") if n.get("CornerRadius") == "10" and n.get("Padding") == "18"]
def has_page_layout(tab):
    return any(p.get("Margin") == "28,24,26,38" for p in tabs[tab].iter(w + "StackPanel"))

version = tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", project).group(1).split(".")))

checks = {
    "group menu groups commands into submenus instead of a long flat list":
        all(f"menu.Items.Add({b}" in populate for b in (
            "BuildTabsMenu(", "BuildSortMenu()", "BuildViewMenu()", "BuildAppearanceMenu()",
            "BuildBehaviorMenu()", "BuildAutomationMenu()", "BuildGroupToolsMenu(")),
    "group delete is isolated at the bottom and always confirmed":
        populate.rsplit("menu.Items.Add(", 1)[1].startswith("deleteFenceItem);") and
        'MakeItem("그룹 삭제…", ConfirmDeleteGroup)' in populate and
        menu.count("DeleteRequested?.Invoke(this)") == 1 and
        "DeleteRequested?.Invoke(this)" in confirm_delete and "MessageBoxResult.No" in confirm_delete,
    "clearing tab rules asks first":
        'MakeItem("현재 탭 규칙 모두 지우기…", ConfirmClearTabRules)' in menu and
        "자동 분류 규칙 지우기" in menu,
    "roll-up command only shown when roll-up is enabled":
        "if (_settings.EnableRollup)" in populate and '"그룹 접기"' in populate,
    "option lists show the current value":
        all(s in menu for s in ('MakeChoiceMenu("색상 테마"', 'MakeChoiceMenu("배경 불투명도"',
                                'MakeChoiceMenu("모서리"', 'MakeCheckItem("오름차순"', 'MakeCheckItem("내림차순"',
                                "IsTheme(", "IsSpacing(", "NearlyEqual(Model.TitleFontSize")),
    "single icon-size list in the View menu":
        menu.count('Header = "아이콘 크기"') == 1 and 'MakeSubMenu("아이콘 크기"' not in menu,
    "no leftover English option labels in the group menu":
        all(s not in menu for s in ('"Violet"', '"Amber"', '"Rose"', '"Small (10)"', '"Normal (12)"',
                                    '"Large (14)"', '"White"', '"Soft gray"', '"Blue"', '"Mint"', '"  Details')),
    "disabled spacing option explains why":
        "ToolTipService.SetShowOnDisabled(iconSpacingMenu, true);" in menu,
    "tray calendar repairs are tagged and nested under a troubleshooting submenu":
        app.count('Tag = "calendar.repair"') == 4 and
        'new Forms.ToolStripMenuItem("캘린더 문제 해결")' in app and
        "case TrayMenuSection.CalendarRepair:" in app and
        'case "calendar.repair": return TrayMenuSection.CalendarRepair;' in routing,
    "calendar repair routing is exercised by the compiled harness":
        '("calendar.repair", "캘린더 표시 복구", TrayMenuSection.CalendarRepair)' in harness,
    "calendar settings start with a title and a close button":
        'var closeTop = MakeButton("달력으로 돌아가기");' in prefs and
        prefs.index("closeTop") < prefs.index('AddPreferencesSectionTitle(panel, "보기")'),
    "calendar settings are split into labelled sections in a logical order":
        all(f'AddPreferencesSectionTitle(panel, "{s}")' in prefs for s in ("보기", "글자 크기", "배경", "공휴일 캘린더", "Google 캘린더")) and
        prefs.index('"공휴일 캘린더"') < prefs.index('"Google 캘린더"') < prefs.index("자동 동기화 간격 (분)"),
    "calendar opacity sliders show their value":
        "idleOpacityValue" in prefs and "activeOpacityValue" in prefs,
    "calendar appearance apply button still persists through the same checkpoint":
        "var appearanceSave = MakeButton(" in prefs and "_onPositionChanged();" in prefs,
    "calendar, Windows icon and about pages use the same card layout as other pages":
        all(has_page_layout(t) and len(cards(t)) >= 2 for t in ("CalendarTab", "SystemIconsTab", "AboutTab")),
    "about page keeps every reset and update control":
        all(any(n.get(x_name) == name for n in tabs["AboutTab"].iter()) for name in (
            "FactoryResetButton", "CompleteLocalResetButton", "ResetAllWindowGeometryButton",
            "ResetPlacementButton", "RestartNormalButton")) and
        # v8.0.40: update controls moved to their own "업데이트·정보" page.
        all(any(n.get(x_name) == name for n in tabs["UpdatesTab"].iter()) for name in (
            "AutomaticUpdateCheck", "BetaUpdateChannelCheck")),
    "menu usability beta version or later":
        version >= (8, 0, 38),
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
