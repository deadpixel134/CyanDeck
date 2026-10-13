#!/usr/bin/env python3
"""v41 desktop calendar placement safety and user-invoked window fallback static checks.
Static source checks only: no Windows HWND, WPF runtime or data file operations.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
read = lambda path: (root / path).read_text(encoding='utf-8-sig')
host = read('src/CyanDeck/Services/DesktopHost.cs')
module = read('src/CyanDeck/Calendar/CalendarModule.cs')
window = read('src/CyanDeck/Calendar/CalendarWindow.cs')
controller = read('src/CyanDeck/Services/AppController.cs')
checks = [
    ('hidden helper guard and class allow list', 'IsHiddenOwnProcessShellHelper' in host and 'GDI+ Hook Window Class' in host and 'WindowsForms10.Window.0.app.' in host),
    ('no blanket PID exemption', 'surfacePid == 0 || surfacePid != candidatePid' in host),
    ('window validity and invisible condition', '!NativeMethods.IsWindow(candidate) || IsWindowVisible(candidate)' in host),
    ('no owned helper bypass', 'NativeMethods.GW_OWNER) != IntPtr.Zero' in host and 'NativeMethods.GetParent(candidate) != IntPtr.Zero' in host),
    ('bounded helper anchor traversal', 'hops < 12' in host and 'GW_HWNDPREV' in host),
    ('no explicit TOPMOST in new helper fallback', 'hwnd, safeAnchor' in host and 'flags & ~NativeMethods.SWP_FRAMECHANGED' in host),
    ('anchor must be valid', 'NativeMethods.IsWindow(safeAnchor)' in host),
    ('anchor and actual lower window in failure diagnostic', 'anchor={DescribeWindow(requestedAnchor)}, below={DescribeWindow(below)}' in host),
    ('post-place strict desktop band verification', '_lastBandMoveSucceeded && IsAlreadyInDesktopBand(hwnd)' in host),
    ('post-place skip only hidden own infrastructure', 'IsHiddenOwnProcessShellHelper(below, hwnd)' in host),
    ('keep original Explorer IME allow list', 'MSCTFIME UI' in host and 'helperPid == explorerPid' in host),
    ('keep reject unrelated windows', 'return false;' in host[host.index('private bool IsAlreadyInDesktopBand'):host.index('private static string DescribeWindow')]),
    ('standalone is user chosen process-only', 'private bool _standaloneWindowMode;' in module and 'TryOpenStandaloneWindow()' in module),
    ('standalone is never automatic on failed placement', 'HideFailedSurface(window, reason, now)' in module and 'TryOpenStandaloneWindow();' not in module[module.index('private void HideFailedSurface'):module.index('private void ScheduleBoundedPlacementRetry')]),
    ('editing and connecting cannot be interrupted', '_window is { IsEditing: true } || _window is { IsGoogleConnecting: true }' in module),
    ('stop pending automatic retry on switch', '_deferredPlacementTimer?.Stop();' in module[module.index('public bool TryOpenStandaloneWindow'):module.index('public string GetTrayDisplayStatus')]),
    ('old desktop hwnd is unregistered', '_host.ForgetInteractiveSurface(_window);' in module[module.index('public bool TryOpenStandaloneWindow'):module.index('public string GetTrayDisplayStatus')]),
    ('normal non-topmost taskbar window', 'WindowStyle.SingleBorderWindow' in window and 'Topmost = false' in window and 'ShowInTaskbar = true;' in window),
    ('normal standalone does not use incompatible transparent layered border', 'AllowsTransparency = !standaloneWindowMode;' in window),
    ('closing standalone hides instead of closing content', 'e.Cancel = true; Hide();' in window),
    ('explicit final close on dispose', '_window.AllowFinalClose();' in module),
    ('desktop mode unchanged', 'EnsureAttachedAfterShellRebuild(force: true);' in module and 'ShowActivated = false;' in module),
    ('no automatic desktop placement when in standalone mode', 'if (window is null || _disposed || _standaloneWindowMode || _attachmentAttemptInProgress)' in module),
    ('show desktop recovery ignored in standalone', 'if (!_standaloneWindowMode)' in module and '_showDesktopRecovery = new DesktopShowDesktopRecovery' in module),
    ('manual-only activation', '_window.Activate(); // Only on the user\'s explicit tray action.' in module),
    ('standalone mode exposed in diagnostics', 'Mode={(_standaloneWindowMode' in module),
    ('tray offers explicit choice', '캘린더 일반 창으로 열기 (표시 실패 시)' in controller and 'new Action(OpenCalendarStandaloneWindow)' in controller),
    ('standalone not offered in safe mode', 'if (!_safeMode && _state.Settings.EnableCalendar)' in controller),
    ('Google and local-only store retained', 'new GoogleCalendarSync(_events, _preferences' in module and 'CalendarEventStore _events = new()' in module),
    ('no global keyboard hooks added', 'WH_KEYBOARD_LL' not in module and 'WH_KEYBOARD_LL' not in host),
    ('no app setting migration', 'public bool TryOpenStandaloneWindow()' in module and '_standaloneWindowMode' not in read('src/CyanDeck/Models/Models.cs')),
]
failed = []
for label, ok in checks:
    print(('PASS: ' if ok else 'FAIL: ') + label)
    if not ok: failed.append(label)
print(f'v41 calendar safety static checks: {len(checks)-len(failed)}/{len(checks)}')
if failed: raise SystemExit(1)
