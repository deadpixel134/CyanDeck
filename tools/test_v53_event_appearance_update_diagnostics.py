#!/usr/bin/env python3
"""Non-destructive v8.0.29 event right-click color and updater UX safety gate."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1]
def get(p): return (root/p).read_text(encoding='utf-8-sig')
win=get('src/CyanDeck/Calendar/CalendarWindow.cs')
styles=get('src/CyanDeck/Calendar/CalendarEventAppearanceStore.cs')
holiday=get('src/CyanDeck/Calendar/KoreanHolidayCalendars.cs')
sync=get('src/CyanDeck/Calendar/GoogleCalendarSync.cs')
ui=get('src/CyanDeck/Views/SettingsWindow.xaml.cs')
xaml=get('src/CyanDeck/Views/SettingsWindow.xaml')
diag=get('src/CyanDeck/Services/UpdateFailureDiagnostics.cs')
eventtests=get('tests/CalendarDomainHarness/Program.cs')
eventproj=get('tests/CalendarDomainHarness/CalendarDomainHarness.csproj')
updatetests=get('tests/CompleteResetHarness/Program.cs')
updateproj=get('tests/CompleteResetHarness/CompleteResetHarness.csproj')
onboarding=get('src/CyanDeck/Services/FirstRunOnboardingPolicy.cs')
menu=win.split('private ContextMenu CreateEventAppearanceMenu(',1)[1].split('private bool CanRelocateEvent(',1)[0]
draw=win.split('private void RenderMonth(',1)[1].split('private ContextMenu CreateEventAppearanceMenu(',1)[0]
checks={
  'calendar right-click menu offered for all event bars':
    'var menu = CreateEventAppearanceMenu(bar.Event);' in draw and
    'line.ContextMenu = menu;' in draw and draw.index('line.ContextMenu = menu;') > draw.index('if (bar.Event.Conflict)'),
  'background and font color submenus both visible':
    '"배경색 변경"' in menu and '"글자색 변경"' in menu,
  'preset palette swatches and custom Windows color picker':
    'System.Windows.Shapes.Rectangle' in menu and 'Forms.ColorDialog' in menu and
    '"사용자 지정 색상…"' in menu,
  'individual and full reset available':
    '"이 색상만 기본값으로 복원"' in menu and '"색상 모두 기본값으로"' in menu and
    'SaveEventAppearance(ev, null, null)' in menu,
  'calendar bar uses per-event appearance before calendar default':
    'appearance.BackgroundColor ??' in draw and 'appearance.TextColor' in draw and
    '_preferences.ColorFor(bar.Event.CalendarId)' in draw,
  'drag ghost uses same event-specific color':
    'eventStyle.BackgroundColor ?? _preferences.ColorFor(ev.CalendarId)' in win and
    'new SolidColorBrush(ghostTextColor)' in win,
  'style changes update only independent local store':
    '_eventAppearance.Set(ev.Id, background, textColor)' in menu and
    all(token not in menu for token in ('_store.CommitEventChange(', 'GoogleCalendarSync', 'RemoteETag =', 'Dirty = true')),
  'read-only and holiday event appearance allowed':
    'var canEdit = !_eventAppearance.IsReadOnly;' in menu and
    'RemoteReadOnly' not in menu and 'KoreanHolidayCalendars.Contains' not in menu,
  'holiday IDs deterministic across feed refresh':
    'Id = DeterministicGuid(' in holiday,
  'read-only event store and Google source protected':
    'Path.Combine(DataRootPaths.ActiveRoaming, "calendar-event-appearance.json")' in styles and
    '_store.UpsertBatch' not in menu and
    'GoogleCalendarSync' not in menu and
    'Dirty = true' not in menu,
  'atomic color file saving with backup and rollback':
    'File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true)' in styles and
    'stream.Flush(flushToDisk: true)' in styles and
    '_styles = snapshot;' in styles,
  'corrupt or missing appearance primary fails closed':
    'IsReadOnly = true' in styles and 'IsRecoveredFromBackup = true' in styles and
    'if (IsReadOnly) throw new IOException(' in styles,
  'validate color codes and reject malformed event ID':
    'color.Length == 7' in styles and 'color.Skip(1).All(Uri.IsHexDigit)' in styles and
    'eventId == Guid.Empty' in styles,
  'first-run guard preserves already-created appearance file':
    '"calendar-event-appearance.json"' in onboarding and
    '"calendar-event-appearance.json.bak"' in onboarding,
  'calendar domain tests cover persistence/no sync effects':
    'originalCalendar.Events.Single().Dirty == false' in eventtests and
    'appearance.Get(holidayId)' in eventtests and
    'File.ReadAllText(eventPath) == originalEventJson' in eventtests,
  'calendar appearance production source compiled in domain test':
    'CalendarEventAppearanceStore.cs' in eventproj,
  'updates page parses with failure summary and log button':
    ET.fromstring(xaml) is not None and
    'UpdateFailureSummaryText' in xaml and 'OpenUpdateFailureLog_Click' in xaml,
  'update diagnostics never shows raw failure log text':
    'Categorize(last)' in diag and 'return new UpdateFailureSummary(status' in diag and
    'private-data-safe' not in diag,
  'bounded update log read and no network':
    '16 * 1024' in diag and 'FileStream(' in diag and
    all(token not in diag for token in ('HttpClient', 'File.Delete(', 'File.WriteAllText(')),
  'update retry cooldown classified locally':
    'FailedUpdateRetryPolicy.RetryDelay' in diag and
    'filename.AsSpan(7, 64)' in diag,
  'manual retry and rollback still available':
    'private async void CheckForUpdates_Click' in ui and
    'private void RollbackUpdate_Click' in ui and 'RefreshUpdateDiagnostics();' in ui,
  'pending update recovery status exposed':
    'UpdateService.HasPendingRecovery()' in ui and
    '미완료 업데이트 복구 기록' in ui,
  'diagnostic test uses disposable local root and checks privacy':
    'UpdateFailureDiagnostics.Inspect(mockLocal, nowUtc)' in updatetests and
    'diagnostic.Text.Contains("token.dat")' in updatetests and
    'UpdateFailureDiagnostics.cs' in updateproj,
  'v8.0.29+ release with calendar schema unchanged':
    tuple(map(int,re.search(r'<Version>(\d+\.\d+\.\d+)</Version>',get('src/CyanDeck/CyanDeck.csproj')).group(1).split('.'))) >= (8,0,29) and
    'Current = 35' in get('src/CyanDeck/Services/StateSchema.cs')
}
for name,ok in checks.items():print(('PASS' if ok else 'FAIL')+': '+name)
print(f'RESULT {sum(checks.values())}/{len(checks)}')
if not all(checks.values()):raise SystemExit(1)
