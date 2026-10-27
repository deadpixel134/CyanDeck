#!/usr/bin/env python3
"""Regression gate: desktop-attached windows must normalize spurious startup hover.

Non-destructive static checks; Windows UI verification remains a separate step.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]

def read(path):
    return (root / path).read_text(encoding="utf-8-sig")

pointer = read("src/CyanDeck/Appearance/DesktopPointerHover.cs")
fence = read("src/CyanDeck/Views/FenceWindow.xaml.cs")
chameleon = read("src/CyanDeck/Appearance/WpfChameleonActivationBridge.cs")
hidden_header = read("src/CyanDeck/Appearance/WpfHiddenHeaderHoverBridge.cs")
calendar = read("src/CyanDeck/Calendar/CalendarWindow.cs")
project = read("src/CyanDeck/CyanDeck.csproj")

checks = {
    "physical cursor is cross-checked with the actual HWND screen bounds":
        all(s in pointer for s in ("element.IsMouseOver", "new WindowInteropHelper(window).Handle",
                                   "NativeMethods.GetWindowRect(hwnd, out var rect)", "Forms.Cursor.Position",
                                   "ContainsScreenPoint(cursor.X, cursor.Y")),
    "outside and degenerate rects are rejected including right/bottom edges":
        all(s in pointer for s in ("right > left && bottom > top", "cursorX >= left",
                                   "cursorX < right", "cursorY >= top", "cursorY < bottom")),
    "fence reconciles presentation after Loaded and desktop-host attachment":
        all(s in fence for s in ("DispatcherPriority.ContextIdle",
                                 "new Action(ReconcileInitialHoverPresentation)",
                                 "private void ReconcileInitialHoverPresentation()",
                                 "ApplyTitlebarMode(pointerInside);",
                                 "UpdateHeaderControls(pointerInside);",
                                 "ApplyItemScrollbars(pointerInside);")),
    "fence filters synthetic MouseEnter and remains pointer-driven":
        "if (!DesktopPointerHover.IsActuallyOver(this))" in fence and
        "ReconcileInitialHoverPresentation();" in fence and
        "private void Window_MouseLeave(" in fence,
    "fence opacity and header do not use stale IsMouseOver":
        "_peekMode || !_settings.EnableChameleon || !Model.AutoFade || DesktopPointerHover.IsActuallyOver(this)" in fence and
        "ApplyTitlebarMode(IsMouseOver)" not in fence and
        "UpdateHeaderControls(IsMouseOver)" not in fence and
        "ApplyItemScrollbars(IsMouseOver)" not in fence,
    "chameleon and hidden group header both use guarded hover":
        "IsPointerOverGroup = DesktopPointerHover.IsActuallyOver(_groupRoot)" in chameleon and
        "&& DesktopPointerHover.IsActuallyOver(_groupRoot)" in hidden_header,
    "calendar re-evaluates opacity at idle after placement":
        "new Action(ApplyCalendarAppearance)" in calendar and
        "DispatcherPriority.ContextIdle" in calendar and
        "|| IsActive || DesktopPointerHover.IsActuallyOver(this);" in calendar,
    "cursor-check does not send input or alter saved user data":
        all(s not in pointer for s in ("SetCursorPos", "SendInput", "File.", "Mouse.Capture",
                                        "Model.", ".Save(", ".Delete(")),
    "beta version incremented":
        "<Version>8.0.37</Version>" in project,
}
for label, ok in checks.items():
    print(("PASS: " if ok else "FAIL: ") + label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()):
    raise SystemExit(1)
