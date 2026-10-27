#!/usr/bin/env python3
"""v8.0.41: follow-up UI/UX fixes (confirmed calendar deletion, working Properties command,
remaining English text, Esc handling, consistent "접기" wording, unused portal menu removed).
Non-destructive static checks; native dialogs are verified on Windows by hand."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

calendar = read("src/CyanDeck/Calendar/CalendarWindow.cs")
fence = read("src/CyanDeck/Views/FenceWindow.xaml.cs")
fence_xaml = read("src/CyanDeck/Views/FenceWindow.xaml")
loose = read("src/CyanDeck/Services/LooseDesktopIconHost.cs")
launcher = read("src/CyanDeck/Services/ShellPropertiesLauncher.cs")
peek = read("src/CyanDeck/Peek/WpfPeekTaskbarButtonWindow.cs")
swap = read("src/CyanDeck/Views/MonitorContentSwapWindow.xaml")
gallery = read("src/CyanDeck/Views/SnapshotGalleryWindow.xaml.cs")
report = read("src/CyanDeck/Views/ProblemReportWindow.cs")
harness = read("tests/FenceVisibilityHarness/Program.cs")
harness_project = read("tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj")
project = read("src/CyanDeck/CyanDeck.csproj")
settings = read("src/CyanDeck/Views/SettingsWindow.xaml.cs")
settings_xaml = read("src/CyanDeck/Views/SettingsWindow.xaml")
dark = read("src/CyanDeck/Themes/DarkControls.xaml")
menu_theme_xaml = read("src/CyanDeck/Themes/DeckMenu.xaml")
menu_theme = read("src/CyanDeck/Views/FenceContextMenuTheme.cs")
group_menu = read("src/CyanDeck/Views/FenceWindow.ContextMenu.cs")

prefs = calendar.split("private void TogglePreferences()", 1)[1].split("private void AddPreferencesSectionTitle(", 1)[0]
save_bar = settings.split("private void RefreshSaveState()", 1)[1].split("private bool PrepareSavedStateForAction(", 1)[0]
live_check = settings.split("private bool HasUnsavedChangesWithoutCommit()", 1)[1].split("private void SettingsWindow_Closing(", 1)[0]

def handler(name):
    body = settings.split(f"void {name}(object sender, RoutedEventArgs e)", 1)[1]
    return body.split("\n    private ", 1)[0]

ASKING_COMMANDS = ("ApplyRulesNow_Click", "AutomaticLayout_Click", "SwapScreenContents_Click",
                   "AddSystemIconGroup_Click", "ResetStandaloneRecycleBin_Click", "CheckForUpdates_Click",
                   "Export_Click", "RestartSafeMode_Click", "RestartNormal_Click",
                   # v8.0.44: rollback restarts the app; restoring a recovery point replaces the state.
                   "RollbackUpdate_Click", "RestoreRecoveryPoint_Click")

delete_click = calendar.split('var delete = MakeButton("일정 삭제");', 1)[1].split("actions.Children.Add(delete);", 1)[0]
editor_keys = calendar.split("string EditorSignature()", 1)[1].split("void FocusInput()", 1)[0]
version = tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", project).group(1).split(".")))

def properties_body(text):
    return text.split("private static void ShowProperties(string path)", 1)[1].split("\n    }", 1)[0]

checks = {
    "calendar event deletion asks first, defaults to No and warns about Google":
        "MessageBoxButton.YesNo" in delete_click and "MessageBoxResult.No" in delete_click and
        "existing.RemoteId is not null" in delete_click and "Google 캘린더에서도 삭제됩니다" in delete_click and
        delete_click.index("MessageBox.Show(") < delete_click.index("_store.Delete(existing)"),
    "Esc works anywhere in the event editor and asks before discarding edits":
        "overlay.KeyDown +=" in editor_keys and "e.Key != Key.Escape" in editor_keys and
        "EditorSignature() != initialSignature" in editor_keys and "MessageBoxResult.No" in editor_keys and
        "overlay.PreviewKeyDown +=" in editor_keys and "input.PreviewKeyDown" not in calendar,
    "Properties uses ShellExecuteEx with SEE_MASK_INVOKEIDLIST":
        "SEE_MASK_INVOKEIDLIST = 0x0000000C" in launcher and 'lpVerb = "properties"' in launcher and
        'EntryPoint = "ShellExecuteExW"' in launcher and "NormalizeTarget(path)" in launcher,
    "group items and loose desktop icons both use it and report failures":
        all("ShellPropertiesLauncher.TryShow(path, out var error)" in properties_body(t) and
            "MessageBox.Show(" in properties_body(t) and 'Verb = "properties"' not in t
            for t in (fence, loose)),
    "properties path handling is exercised by the compiled harness":
        "ShellPropertiesLauncher.NormalizeTarget(" in harness and
        'Include="../../src/CyanDeck/Services/ShellPropertiesLauncher.cs"' in harness_project,
    "no English text left in the Peek preview or the empty group placeholder":
        "Click the taskbar button" not in peek and "Drop files here" not in fence_xaml and
        'Text="파일을 여기에 놓으세요"' in fence_xaml,
    "Esc closes the screen swap, snapshot gallery and problem report windows":
        'Content="취소" IsCancel="True"' in swap and
        "System.Windows.Input.Key.Escape" in gallery and "IsCancel = true" in report,
    "roll-up wording matches the menus and settings":
        "말아 올리기" not in fence_xaml and 'ToolTip="그룹 접기/펼치기"' in fence_xaml,
    "unused English portal icon-size menu module removed":
        not (root / "src/CyanDeck/FolderPortals/WpfPortalViewIconSizeMenuBridge.cs").exists() and
        not (root / "src/CyanDeck/FolderPortals/PortalViewIconSizeCoordinator.cs").exists(),
    # Calendar panel: one model, no buttons that only save.
    "calendar settings have no Apply or Save buttons; options save as they change":
        "appearanceSave" not in calendar and 'MakeButton("간격 저장")' not in calendar and
        "MakeButton(\"달력으로 돌아가기\")" not in calendar and
        all(f"{c} +=" in prefs for c in ("viewMode.SelectionChanged", "rows.SelectionChanged",
            "dateFont.ValueChanged", "eventFont.ValueChanged", "chameleon.Checked",
            "idleOpacity.ValueChanged", "activeOpacity.ValueChanged", "autoSync.SelectionChanged")) and
        prefs.count("SaveCalendarSetting(") >= 8,
    "calendar header stays fixed with one primary finish button":
        "DockPanel.SetDock(top, Dock.Top);" in prefs and 'MakePrimaryButton("완료")' in prefs and
        prefs.count("MakePrimaryButton(") == 1,
    "sync interval is a list of presets that keeps a custom earlier value":
        "_preferences.UpdateAutoSyncInterval(minutes);" in prefs and "(사용자 지정)" in prefs and
        "CalendarSyncIntervalPolicy.IsValid(currentSyncMinutes)" in prefs,
    "value helpers in the calendar panel look like links, not like save buttons":
        all(f'MakeLinkButton("{t}")' in calendar for t in ("모두 표시", "모두 숨김", "기본색")),
    # Settings window: a single explicit save point.
    "settings footer has one primary save button and a close button":
        'x:Name="SaveButton" Content="저장" Click="Apply_Click" Style="{StaticResource DeckPrimaryButtonStyle}"' in settings_xaml and
        'Content="닫기" IsCancel="True" Click="Cancel_Click"' in settings_xaml and
        'Content="확인"' not in settings_xaml and 'Click="Ok_Click"' not in settings_xaml and
        "Key.S" in settings and "SaveFromSaveBar();" in settings,
    "save bar shows unsaved edits live without touching half-typed values":
        "HasUnsavedChangesWithoutCommit()" in save_bar and "SaveCurrentGroupDraft" not in live_check.split("private void ", 1)[0] and
        "BuildEditorDraft(_currentFence)" in live_check and "QueueSaveStateRefresh" in settings,
    "page commands never save pending edits silently":
        all("PrepareSavedStateForAction(" in handler(h) and "ApplyChanges()" not in handler(h) for h in ASKING_COMMANDS) and
        "MessageBoxButton.YesNoCancel" in settings.split("private bool PrepareSavedStateForAction(", 1)[1].split("\n    }", 1)[0],
    "button roles are visibly different (primary, danger, link)":
        all(f'x:Key="{k}"' in dark for k in ("DeckPrimaryButtonStyle", "DeckDangerButtonStyle", "DeckLinkButtonStyle")) and
        settings_xaml.count('Style="{StaticResource DeckDangerButtonStyle}"') >= 4 and
        "지금 자동 분류 규칙 적용" not in settings_xaml and "자동 분류 규칙 지금 실행" in settings_xaml,
    # Context menus: one dark theme with its own templates, high contrast kept.
    "menus use complete dark templates including submenu popups and separators":
        all(s in menu_theme_xaml for s in ('x:Key="DeckContextMenuStyle"', 'x:Key="DeckMenuItemStyle"',
            "{x:Static MenuItem.SeparatorStyleKey}", 'x:Name="PART_Popup"', 'AllowsTransparency="True"',
            'Property="Role" Value="SubmenuHeader"', 'Value="danger"', 'Value="section"')),
    "menu theme keeps high contrast and the open-menu accounting":
        "if (SystemParameters.HighContrast) return false;" in menu_theme and
        "Interlocked.Increment(ref _openContextMenuCount);" in menu_theme and
        "Interlocked.Decrement(ref _openContextMenuCount);" in menu_theme and
        "DeckThemeResources.MenuStyle(" in menu_theme,
    "group menu shows shortcuts in their own column and marks delete as dangerous":
        'WithGesture(MakeItem("검색…", ShowSearch), "Ctrl+F")' in group_menu and "   Ctrl+F" not in group_menu and
        "deleteFenceItem.Tag = MenuDangerTag;" in group_menu and "Tag = MenuSectionTag" in group_menu,
    "calendar event and loose desktop icon menus share the theme":
        "FenceContextMenuTheme.ApplyTheme(menu)" in calendar and "FenceContextMenuTheme.ApplyTheme(menu)" in loose and
        "FromRgb(30, 32, 37)" not in loose,
    "follow-up beta version or later":
        version >= (8, 0, 41),
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
