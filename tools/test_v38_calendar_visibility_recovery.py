#!/usr/bin/env python3
"""Source-level regression checks only; does not perform Windows GUI testing."""
from pathlib import Path
r=Path(__file__).resolve().parents[1]
read=lambda p:(r/p).read_text(encoding='utf-8-sig')
mod=read('src/DesktopDeck/Calendar/CalendarModule.cs')
host=read('src/DesktopDeck/Services/DesktopHost.cs')
loose=read('src/DesktopDeck/Services/LooseDesktopIconHost.cs')
app=read('src/DesktopDeck/Services/AppController.cs')
attach=mod.split('public void EnsureAttachedAfterShellRebuild(bool force = false)',1)[1].split('public async Task SynchronizeAsync()',1)[0]
schedule=mod.split('private void ScheduleBoundedPlacementRetry(',1)[1].split('private void OnDeferredPlacementRetry(',1)[0]
tick=mod.split('private void OnDeferredPlacementRetry(',1)[1].split('public void RetryHiddenSurface()',1)[0]
recycle=loose.split('private void ApplyVisibility(Entry entry)',1)[1].split('private bool ShouldRender(',1)[0]
zorder=host.split('private bool TryReassertFenceZOrder(',1)[1].split('private bool IsAlreadyInDesktopBand(',1)[0]
checks={
 'only three delayed attempts': 'TimeSpan.FromSeconds(15)' in mod and 'TimeSpan.FromSeconds(45)' in mod and 'TimeSpan.FromMinutes(2)' in mod,
 'retry count strictly bounded': '_deferredPlacementAttempts >= DeferredPlacementDelays.Length' in schedule,
 'one dispatcher retry timer': '_deferredPlacementTimer ??= new DispatcherTimer(DispatcherPriority.Background);' in schedule,
 'retry only for desktop band failures': 'reason.Contains("Cannot enter desktop Z-order band"' in schedule,
 'no tray focus or automatic topmost': 'Topmost = true' not in attach and 'Activate()' not in tick and 'HWND_TOPMOST' not in attach,
 'WPF Show only after desktop attachment': attach.index('TryAttachInteractiveSurface(window, out var reason)') < attach.index('window.Show()'),
 'Show revalidated before opacity restored': attach.index('TryAttachInteractiveSurface(window, out var postShowReason)') < attach.index('window.Opacity = double.IsFinite'),
 'failure remains invisible': 'window.Opacity = 0;' in attach and 'if (window.IsVisible) window.Hide();' in attach,
 'manual repair cooldown retained': 'ManualRepairCooldown = TimeSpan.FromSeconds(15)' in mod,
 'retry callback bypasses latch only explicitly': 'EnsureAttachedAfterShellRebuild(force: true)' in tick,
 'success stops deferred timer': '_deferredPlacementTimer?.Stop();' in attach,
 'dispose stops retry': '_deferredPlacementTimer?.Stop();' in mod.split('public void Dispose()',1)[1],
 'no undocumented Explorer WorkerW creation': 'NativeMethods.SendMessageTimeout(' not in host,
 'less invasive immediate retry only on error 5': 'frameChanged && _lastBandMoveError == 5' in zorder,
 'no stacking above other apps': 'NativeMethods.HWND_TOPMOST' not in zorder and 'NativeMethods.HWND_BOTTOM' in zorder,
 'recycle repeated failures throttled': 'RecyclePlacementCooldown = TimeSpan.FromSeconds(30)' in loose and '_recyclePlacementRetryAfterUtc' in recycle,
 'user tray gets meaningful status': '자동 표시 복구 예약됨' in mod and any(label in app for label in ('v38 표시 복구', 'v41 표시 복구')),
 'calendar local-only and Google code retained': Path(r/'src/DesktopDeck/Calendar/GoogleCalendarSync.cs').exists() and 'LocalOnly' in read('src/DesktopDeck/Calendar/GoogleCalendarSync.cs'),
 'schema remains 35': 'Current = 35;' in read('src/DesktopDeck/Services/StateSchema.cs'),
}
for title,passed in checks.items():
 print(('PASS' if passed else 'FAIL')+': '+title)
print(f'v38 calendar visibility checks: {sum(checks.values())}/{len(checks)}')
if not all(checks.values()): raise SystemExit(1)
