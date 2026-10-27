#!/usr/bin/env python3
"""v8.0.35: restore today-centered weeks only when entering rolling view."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
def read(name): return (root/name).read_text(encoding="utf-8-sig")
window=read("src/CyanDeck/Calendar/CalendarWindow.cs")
policy=read("src/CyanDeck/Calendar/CalendarViewportPolicy.cs")
harness=read("tests/CalendarDomainHarness/Program.cs")
module=read("src/CyanDeck/Calendar/CalendarModule.cs")
calc=policy.split("public static DateTime AnchorAfterViewModeChange(",1)[1].split("public static DateTime Navigate(",1)[0]
render=window.split("public void RenderMonth()",1)[1].split("var viewport = CalendarViewportPolicy.Calculate(",1)[0]
prefs=window.split("var appearanceSave = MakeButton(",1)[1].split("panel.Children.Add(appearanceSave)",1)[0]
checks={
    "pure policy supports switched old and new modes":
        "DateTime currentAnchor, string? previousMode, string? selectedMode, DateTime today" in policy,
    "entering rolling mode uses fresh local current date":
        'selectedMode != "Month"' in calc and 'return today.Date;' in calc,
    "policy preserves the original anchor for unchanged modes and monthly browsing":
        "previousMode is not null" in calc and
        "previousMode, selectedMode" in calc and "return currentAnchor;" in calc,
    "only actual last-rendered view mode is tracked":
        "private string? _lastRenderedViewMode;" in window and
        "_lastRenderedViewMode = _settings.CalendarViewMode;" in render,
    "render adjusts anchor before calculating viewport":
        "AnchorAfterViewModeChange(" in render and
        render.index("AnchorAfterViewModeChange(") < render.index("CancelEventMove();"),
    "preferences apply retains the existing view persistence and render":
        "_settings.CalendarViewMode = " in prefs and
        "_settings.CalendarVisibleWeekRows = " in prefs and
        "_onPositionChanged();" in prefs and
        "RenderMonth();" in prefs,
    "external environment-settings redraw continues the same logic":
        "public void ApplyViewAndAppearanceFromSettings()" in window and
        "if (!_editing && !_showingPreferences) RenderMonth();" in window and
        "public void ApplySettings() => _window?.ApplyViewAndAppearanceFromSettings();" in module,
    "Today button still resets directly":
        "public void GoToToday() { _visibleMonth = DateTime.Today; RenderMonth(); }" in window,
    "month navigation still uses selected mode":
        "CalendarViewportPolicy.Navigate(_visibleMonth, _settings.CalendarViewMode" in window,
    "real six-week viewport checked against Today":
        "rollingFromModeChange == rollingFromTodayButton" in harness and
        '"six-week rolling mode places today in the third visible week"' in harness,
    "arbitrary paging and reverse switch are not reset":
        '"reapplying rolling settings does not discard manually paged viewport"' in harness and
        '"switching to month view preserves the current date anchor"' in harness,
    "variable row counts tested":
        "new[] { 3, 5, 6, 8 }" in harness,
    "first rendering preserves its anchor":
        '"initial render never unexpectedly resets a saved or caller-provided anchor"' in harness,
    "no saved calendar events or google synchronization mutated":
        all(x not in render for x in ("_store.Save", "_sync(", "_preferences.Save", "File.Delete")),
    "beta version bumped":
        tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", read("src/CyanDeck/CyanDeck.csproj")).group(1).split("."))) >= (8, 0, 35)
}
for label,ok in checks.items():print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
