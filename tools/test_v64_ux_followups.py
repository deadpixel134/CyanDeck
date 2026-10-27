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
    "follow-up beta version or later":
        version >= (8, 0, 41),
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
