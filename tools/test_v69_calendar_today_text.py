#!/usr/bin/env python3
"""v8.0.44: the calendar keeps "today" current across midnight, clock changes and resume
from sleep, and its text stays fully opaque while only the background follows the
idle / in-use opacity sliders over their whole range.
Non-destructive static checks; the real midnight/resume behaviour is verified on Windows."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

window = read("src/CyanDeck/Calendar/CalendarWindow.cs")
module = read("src/CyanDeck/Calendar/CalendarModule.cs")
policies = read("src/CyanDeck/Calendar/CalendarDisplayPolicies.cs")
watcher = read("src/CyanDeck/Calendar/CalendarDayRolloverWatcher.cs")
harness = read("tests/CalendarDomainHarness/Program.cs")
harness_project = read("tests/CalendarDomainHarness/CalendarDomainHarness.csproj")

refresh = window.split("private bool RefreshTodayIfDateChanged()", 1)[1].split("private void ApplyTodayRolloverBeforeRender()", 1)[0]
rollover = window.split("private void ApplyTodayRolloverBeforeRender()", 1)[1].split("private static readonly Brush Panel", 1)[0]
render = window.split("public void RenderMonth()", 1)[1].split("var viewport = CalendarViewportPolicy.Calculate(", 1)[0]
appearance = window.split("public void ApplyCalendarAppearance()", 1)[1].split("private void StretchWeekRowsToViewport()", 1)[0]
attach = module.split("public void EnsureAttachedAfterShellRebuild(bool force = false)", 1)[1].split("public async Task SynchronizeAsync()", 1)[0]
rollover_policy = policies.split("public static class CalendarTodayRefreshPolicy", 1)[1].split("public static class CalendarOpacityPolicy", 1)[0]
opacity_policy = policies.split("public static class CalendarOpacityPolicy", 1)[1]

checks = {
    # #13 today refresh
    "next check is just after local midnight, recomputed and capped at one hour":
        "var delay = now.Date.AddDays(1) - now + MidnightMargin;" in rollover_policy and
        "MaximumCheckInterval = TimeSpan.FromHours(1);" in rollover_policy and
        "BusyRetryInterval = TimeSpan.FromSeconds(30);" in rollover_policy,
    "viewport follows today only when the old today was being shown":
        "public static bool ShouldFollowToday(DateTime anchor, string? mode, int configuredRows, DateTime previousToday)" in rollover_policy and
        "anchor.Year == previous.Year && anchor.Month == previous.Month" in rollover_policy and
        "previous >= viewport.FirstSunday && previous <= lastDate" in rollover_policy,
    "watcher listens to the midnight timer, clock/time-zone changes and resume":
        "SystemEvents.TimeChanged += OnSystemTimeChanged;" in watcher and
        "SystemEvents.PowerModeChanged += OnPowerModeChanged;" in watcher and
        "e.Mode == PowerModes.Resume" in watcher and
        "TimeZoneInfo.ClearCachedData();" in watcher and
        "Arm(CalendarTodayRefreshPolicy.DelayUntilNextCheck(DateTime.Now));" in watcher,
    "system events are marshalled to the calendar dispatcher":
        "_dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>" in watcher,
    "busy calendar retries later instead of interrupting":
        ": CalendarTodayRefreshPolicy.BusyRetryInterval);" in watcher and
        "if (_editing || _showingPreferences || _isMoving || _moveCandidate is not null || _openEventMenus > 0)" in refresh and
        refresh.index("return false;") < refresh.index("RenderMonth();"),
    "date change redraws today and moves only a viewport showing the old today":
        "if (today == _lastObservedToday) return true;" in refresh and
        "RenderMonth();" in refresh and
        "if (today == _lastObservedToday) return;" in rollover and
        "_lastObservedToday = today;" in rollover and
        "CalendarTodayRefreshPolicy.ShouldFollowToday(_visibleMonth, _settings.CalendarViewMode," in rollover and
        "_visibleMonth = today;" in rollover,
    "every render (closing the editor/settings included) applies a pending date change before the anchor is used":
        "ApplyTodayRolloverBeforeRender();" in render and
        render.index("if (_editing) return;") < render.index("ApplyTodayRolloverBeforeRender();") <
        render.index("AnchorAfterViewModeChange(") and
        "if (_preserveMoveOnRender) return;" in rollover,
    "a rollover never jumps the week list under the pointer or resets a same-period scroll":
        "if (follow && DesktopPointerHover.IsActuallyOver(this)) return false;" in refresh and
        "double scrollOffset = _weekScroll.VerticalOffset;" in refresh and
        "if (!follow) _weekScroll.ScrollToVerticalOffset(scrollOffset);" in refresh and
        refresh.index("RenderMonth();") < refresh.index("_weekScroll.ScrollToVerticalOffset(scrollOffset);"),
    "watcher unsubscribes from static system events when the calendar closes":
        "_dayRolloverWatcher = new CalendarDayRolloverWatcher(Dispatcher, RefreshTodayIfDateChanged);" in window and
        "Closed += (_, _) => _dayRolloverWatcher?.Dispose();" in window and
        "SystemEvents.TimeChanged -= OnSystemTimeChanged;" in watcher and
        "SystemEvents.PowerModeChanged -= OnPowerModeChanged;" in watcher,
    "today refresh never saves events or settings":
        all(s not in refresh + rollover for s in ("_store.Save", "_sync(", "_preferences.Save", "_onPositionChanged(")) and
        all(s not in watcher for s in (".Save(", "File.")),
    # #15 opaque text, background-only opacity
    "calendar window is fully opaque in every reveal path":
        "public const double WindowOpacity = 1.0;" in opacity_policy and
        "Opacity = CalendarOpacityPolicy.WindowOpacity;" in window and
        "Opacity = settings.CalendarOpacity;" not in window and
        module.count("Opacity = CalendarOpacityPolicy.WindowOpacity;") == 2 and
        "Math.Clamp(_settings.CalendarOpacity" not in module,
    "placement still reveals only after desktop attachment (hidden at zero first)":
        "window.Opacity = 0;" in attach and
        attach.index("TryAttachInteractiveSurface(window, out var postShowReason)") <
        attach.index("window.Opacity = CalendarOpacityPolicy.WindowOpacity;"),
    "slider value is the final background opacity (no division by the hidden 86% value)":
        "CalendarOpacityPolicy.BackgroundOpacity(full," in appearance and
        "_settings.CalendarActiveBackgroundOpacity, _settings.CalendarIdleOpacity);" in appearance and
        "activeBackground / active" not in appearance and "idle / active" not in appearance and
        "_settings.CalendarOpacity" not in appearance,
    "chameleon presentation condition is unchanged":
        "|| _isMoving || _openEventMenus > 0 || DesktopPointerHover.IsActuallyOver(this);" in appearance,
    "background opacity clamps to the slider range with the old defaults":
        "Math.Clamp(value, MinimumBackgroundOpacity, 1.0)" in opacity_policy and
        "DefaultIdleBackgroundOpacity = 0.38;" in opacity_policy and
        "DefaultActiveBackgroundOpacity = 0.86;" in opacity_policy and
        "CalendarOpacityPolicy.TodayBackgroundOpacity(opacity);" in appearance,
    "both policies are exercised by the calendar harness":
        'Include="../../src/CyanDeck/Calendar/CalendarDisplayPolicies.cs"' in harness_project and
        '"rolling view paged away from today keeps the chosen period"' in harness and
        '"month view browsing another month is not moved"' in harness and
        '"in-use background opacity reaches 100% and changes above 86%"' in harness,
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
