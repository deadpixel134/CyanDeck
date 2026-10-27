#!/usr/bin/env python3
"""v8.0.40: settings findability and the UI/UX review fixes (unsaved-change prompt,
confirmed snapshot deletion, readable tray labels, rename/portal/rule input fixes,
calendar drag across periods and the calendar background picker).
Non-destructive static checks; behaviour is exercised by the compiled harness and by hand."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

settings = read("src/CyanDeck/Views/SettingsWindow.xaml.cs")
xaml = read("src/CyanDeck/Views/SettingsWindow.xaml")
app = read("src/CyanDeck/Services/AppController.cs")
gallery = read("src/CyanDeck/Views/SnapshotGalleryWindow.xaml.cs")
prompt = read("src/CyanDeck/Views/TextPrompt.cs")
rules = read("src/CyanDeck/Services/RuleEngine.cs")
fence = read("src/CyanDeck/Views/FenceWindow.xaml.cs")
calendar = read("src/CyanDeck/Calendar/CalendarWindow.cs")
harness = read("tests/FenceVisibilityHarness/Program.cs")
harness_project = read("tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj")
project = read("src/CyanDeck/CyanDeck.csproj")

closing = settings.split("private void SettingsWindow_Closing(", 1)[1].split("private void CloseWithoutPrompt()", 1)[0]
apply = settings.split("private bool ApplyChanges()", 1)[1].split("private void ", 1)[0]
activate = calendar.split("private void ActivateLongPressMove()", 1)[1].split("private void OnMoveMouseMotion(", 1)[0]
render = calendar.split("public void RenderMonth()", 1)[1].split("var viewport = CalendarViewportPolicy.Calculate(", 1)[0]
cancel = calendar.split("private void CancelEventMove()", 1)[1].split("public void ApplyCalendarAppearance()", 1)[0]
edge = calendar.split("private void OnMoveEdgeTick()", 1)[1].split("private static bool CanBuildMoveDraft(", 1)[0]
navigate = calendar.split("private void NavigateDuringMove(int direction)", 1)[1].split("private static bool CanBuildMoveDraft(", 1)[0]
prefs = calendar.split("private void TogglePreferences()", 1)[1].split("private void AddPreferencesSectionTitle(", 1)[0]
version = tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", project).group(1).split(".")))

checks = {
    "settings search covers the wording inside each section and cycles with Enter":
        "BuildSearchIndex()" in settings and "FlashSearchHit(" in settings and
        "SearchAliases" in settings and 'KeyDown="SettingsSearchBox_KeyDown"' in xaml and
        "_searchCursor = (_searchCursor + 1) % visible.Count;" in settings,
    "closing with unsaved edits asks to save, discard or keep editing":
        "Closing += SettingsWindow_Closing;" in settings and
        "MessageBoxButton.YesNoCancel" in closing and "e.Cancel = true;" in closing and
        "HasUnsavedChanges()" in closing and "if (applied) CaptureUnsavedChangesBaseline();" in apply,
    "the unsaved-change prompt never blocks application exit or Windows logoff":
        "!userRequested" in closing and "SC_CLOSE" in settings and
        "_userRequestedClose = true;" in settings.split("private void Cancel_Click(", 1)[1].split("}", 1)[0],
    "OK and commands that close after saving do not ask again":
        "_closeWithoutPrompt = true; DialogResult = true;" in settings and
        settings.count("CloseWithoutPrompt();") >= 5,
    "invalid calendar size is reported instead of silently ignored":
        "if (!TryReadCalendarSize(out var calendarWidth, out var calendarHeight)) return false;" in apply and
        'NavigateToSettingsAnchor("CalendarTab|CalendarSection")' in settings,
    "snapshot deletion is confirmed in the gallery and in the tray":
        "MessageBoxButton.YesNo" in gallery and "MessageBoxResult.No" in gallery and
        "QueueTrayDialog(ConfirmDeleteAllSnapshots)" in app and
        "private void ConfirmDeleteAllSnapshots()" in app,
    "tray commands are in Korean and recovery points show readable labels":
        '"Peek: 창 위로 그룹 보기"' in app and '"바탕화면 보기 (모든 창 최소화)"' in app and
        "Desktop Display" not in app and
        "RecoveryCheckpointLabelPolicy.Describe(" in app and "ToolTipText = file.Name" in app,
    "text prompt opens over its owner, sizes to content and blocks empty input":
        "PlaceOverOwner(window, owner);" in prompt and "SizeToContent = SizeToContent.Height" in prompt and
        "ok.IsEnabled = !empty;" in prompt,
    "rule weekdays accept Korean names":
        "RuleWeekdayParser.Matches(pattern, created.DayOfWeek)" in rules and "weekdayCases" in harness,
    "rename explains Windows name rules":
        "WindowsFileNamePolicy.GetProblem(newName)" in fence,
    "folder portal moves across drives by copy and recycle, same drive by rename":
        "IsSameVolume(sourceFull, destination)) Directory.Move(sourceFull, destination);" in fence and
        "MoveDirectoryAcrossVolumes(sourceFull, destination);" in fence and
        "RecycleOption.SendToRecycleBin" in fence.split("private static void MoveDirectoryAcrossVolumes(", 1)[1].split("\n    }", 1)[0],
    "first real hover always shows the group controls":
        "PreviewMouseMove += Fence_HoverMouseMove;" in fence and "ApplyPointerEnteredPresentation();" in fence,
    "dragging an event scrolls and switches period at the edges without dropping the drag":
        "_moveEdgeTimer.Start();" in activate and "if (!_preserveMoveOnRender) CancelEventMove();" in render and
        "_moveEdgeTimer.Stop();" in cancel and "NavigateDuringMove(direction);" in edge and
        "CalendarViewportPolicy.Navigate(" in navigate and "_body.Children.Add(layer);" in navigate,
    "edge handling never writes event data":
        all(s not in edge for s in ("_store.", "UpsertBatch", "Persist")),
    "calendar settings background colour has a picker and is still saved by Apply":
        "_preferencesBackgroundPicker = new CalendarColorPicker(" in prefs and
        prefs.index("_preferencesBackgroundPicker") < prefs.index("var appearanceSave = MakeButton(") and
        "_settings.CalendarBackgroundColor = baseColor.Text.Trim().ToUpperInvariant();" in prefs,
    "new pure policies are compiled into the harness":
        all(f'Include="../../src/CyanDeck/Services/{n}.cs"' in harness_project for n in (
            "RuleWeekdayParser", "WindowsFileNamePolicy", "RecoveryCheckpointLabelPolicy")),
    "settings findability beta version or later":
        version >= (8, 0, 40),
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
