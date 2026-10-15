#!/usr/bin/env python3
"""v8.0.30: non-invasive calendar blank-space gesture-help regression."""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1]
def read(name): return (root/name).read_text(encoding="utf-8-sig")
window = read("src/CyanDeck/Calendar/CalendarWindow.cs")
policy = window.split("private const string EmptyDateInteractionHelp =",1)[1].split(";",1)[0]
date_cells = window.split("var dayCell = new Border",1)[1].split("foreach (var bar in bars)",1)[0]
events = window.split("foreach (var bar in bars)",1)[1].split("StretchWeekRowsToViewport();",1)[0]
hold = window.split("private void BeginEventMoveHold(",1)[1].split("private void OnMoveMouseMotion(",1)[0]
scroll = window.split("_weekScroll = new ScrollViewer",1)[1].split("};",1)[0]
checks = {
    "blank calendar tooltip describes adding an event by double click":
        "빈 날짜 더블클릭" in policy and "새 일정 추가" in policy,
    "tooltip describes editing existing events with read-only limitation":
        "기존 일정 더블클릭" in policy and "읽기 전용 일정 제외" in policy,
    "tooltip accurately scopes right-click style changes to event bars":
        "일정 막대 우클릭" in policy and "배경색·글자색 변경" in policy,
    "tooltip accurately describes one-second hold-drag on editable event":
        "수정 가능한 일정 막대" in policy and "왼쪽 버튼으로 1초" in policy and "날짜 이동" in policy,
    "tooltip retains mouse wheel and touch scrolling":
        "마우스 휠·터치 스크롤" in policy and "다른 주 보기" in policy,
    "scroll surface uses shared tooltip instead of old text":
        "ToolTip = EmptyDateInteractionHelp" in scroll and
        "마우스 휠이나 터치 스크롤로 다른 주" not in window,
    "blank day cell exposes shared tooltip":
        "ToolTip = EmptyDateInteractionHelp" in date_cells,
    "date number exposes same tooltip":
        date_cells.count("ToolTip = EmptyDateInteractionHelp") == 2,
    "empty day and date number double clicks still open editor":
        date_cells.count("e.ClickCount != 2") >= 2 and
        date_cells.count("BeginEditing(date, null)") >= 2,
    "event bars still offer right-click event-style context menu":
        "CreateEventAppearanceMenu(bar.Event)" in events and "line.ContextMenu = menu" in events,
    "event editing still restricted for read-only remote entries":
        "if (existing?.RemoteReadOnly == true)" in window,
    "hold timer remains one second":
        "Interval = TimeSpan.FromSeconds(1)" in window and
        "Mouse.LeftButton != MouseButtonState.Pressed" in hold,
    "drag retains write/permission checks":
        "CanRelocateEvent(ev, out var reason)" in hold and
        "if (_moveCandidate is not" in hold,
    "footer hint no longer contradicts one-second implementation":
        "2초 누르기" not in window and "일정 1초 누른 채 드래그" in window,
    "touch viewport scroll support remains":
        "PanningMode = PanningMode.VerticalOnly" in scroll and
        "VerticalScrollBarVisibility = ScrollBarVisibility.Hidden" in scroll,
    "no changes to original Windows Desktop or Google sync operations":
        not any(x in policy for x in ("File.Delete", "GoogleCalendarSync", "SetNativeDesktopIconsVisible")),
    "calendar schema remains unchanged":
        "Current = 35" in read("src/CyanDeck/Services/StateSchema.cs"),
    "next beta version marked 8.0.30":
        tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", read("src/CyanDeck/CyanDeck.csproj")).group(1).split("."))) >= (8, 0, 30)
}
for name, passed in checks.items():
    print(("PASS: " if passed else "FAIL: ") + name)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
