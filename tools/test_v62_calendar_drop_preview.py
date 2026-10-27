#!/usr/bin/env python3
"""v8.0.39: the one-second hold-drag shows where a calendar event will land
before release. Non-destructive static checks; visual verification is separate."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
def read(name): return (root / name).read_text(encoding="utf-8-sig")

window = read("src/CyanDeck/Calendar/CalendarWindow.cs")
project = read("src/CyanDeck/CyanDeck.csproj")

activate = window.split("private void ActivateLongPressMove()", 1)[1].split("private void OnMoveMouseMotion(", 1)[0]
position = window.split("private void UpdateMovePosition(Point point)", 1)[1].split("private void UpdateMoveDropPreview(", 1)[0]
preview = window.split("private void UpdateMoveDropPreview(Point point)", 1)[1].split("private DateTime? GetDateAtPosition(", 1)[0]
release = window.split("private void OnMoveMouseRelease(", 1)[1].split("private void CancelEventMove()", 1)[0]
cancel = window.split("private void CancelEventMove()", 1)[1].split("public void ApplyCalendarAppearance()", 1)[0]
version = tuple(map(int, re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", project).group(1).split(".")))

checks = {
    "drag layer carries a date label next to the ghost":
        "_moveLayer.Children.Add(_moveDropBadge);" in activate and "_moveAccentColor = color;" in activate,
    "every pointer move refreshes the drop preview":
        "UpdateMoveDropPreview(point);" in position,
    "preview uses the same drop date as the release":
        "var date = _moveDropDate?.Date;" in preview and "var date = _moveDropDate;" in release,
    "multi-day events highlight their whole destination span":
        "ev.EffectiveEndDate.Date - ev.Date.Date" in preview and
        "RebuildMoveTargetHighlights(date, spanDays, tone);" in preview and
        "target.AddDays(spanDays)" in preview,
    "highlights are clipped to the visible weeks and drawn under the ghost":
        "rect.Intersect(visible);" in preview and "_moveLayer.Children.Insert(0, highlight);" in preview,
    "outside, same-date and impossible-time drops are announced distinctly":
        "달력 밖 · 놓으면 이동 취소" in preview and "같은 날짜 · 변경 없음" in preview and
        "CanBuildMoveDraft(ev, target)" in preview and "CalendarEventMovePolicy.CreateDraft(ev, target)" in preview,
    "preview is rebuilt only when the hovered date changes":
        "date != _movePreviewDate" in preview,
    "preview never writes event data":
        all(s not in preview for s in ("_store.", "UpsertBatch", "_preferences.Set", "Persist")),
    "release still commits through the existing move policy":
        "_store.UpsertBatch(CalendarEventMovePolicy.CreateDraft(ev, date.Value), ev);" in release,
    "cancel clears all preview state":
        all(s in cancel for s in ("_moveTargetHighlights.Clear();", "_moveDropBadge = null;",
                                  "_movePreviewDate = null;", "_movePreviewShown = false;")),
    "hold-to-drag still requires the one-second timer":
        "Interval = TimeSpan.FromSeconds(1)" in window,
    "calendar drop preview beta version or later":
        version >= (8, 0, 39),
}
for label, ok in checks.items(): print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
