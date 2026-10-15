#!/usr/bin/env python3
"""Non-destructive static guard for tray-only combined surface recovery."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
def read(p):return (root/p).read_text(encoding='utf-8-sig')
app=read('src/CyanDeck/Services/AppController.cs')
policy=read('src/CyanDeck/Services/DisplaySurfaceRecoveryPlan.cs')
harness=read('tests/FenceVisibilityHarness/Program.cs')
csproj=read('tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj')
appMenu=app.split('private void PopulateTrayMenu(',1)[1].split('private void CompactTrayMenu(',1)[0]
repair=app.split('private void RecoverVisibleSurfacesFromTray()',1)[1].split('private void RecoverDesktopVisibility()',1)[0]
checks={
 'tray command exists before calendar-only early return':
   appMenu.index('menu.Items.Add("표시 상태 복구"') < appMenu.index('if (!_safeMode && !_state.Settings.EnableCyanDeck)'),
 'only one tray unified recovery command': app.count('menu.Items.Add("표시 상태 복구"')==1,
 'tray recovery is not on automatic monitor transition callbacks':
   app.count('RecoverVisibleSurfacesFromTray)')==1,
 'safe mode excludes manual combined action': 'if (!_safeMode && (_state.Settings.EnableCyanDeck || _state.Settings.EnableCalendar))' in appMenu,
 'manual plan considers runtime and stable display': 'DisplaySurfaceRecoveryPlan.Create(' in repair and
     '_desktopRuntimeStarted' in repair and 'IsStableForUserLayoutAction != false' in repair,
 'manual recovery blocks live display transition': 'if (plan.BlockedByDisplayTransition)' in repair,
 'group visibility restored only when opted in': 'if (plan.RepairGroups)' in repair and 'RecoverDesktopVisibility();' in repair,
 'calendar-only recovery does not require group runtime': 'deckEnabled && desktopRuntimeReady' in policy and 'calendarEnabled' in policy,
 'healthy calendar avoids force placement': '_calendar?.IsSurfaceVisible == true' in repair and
     repair.index('_calendar?.IsSurfaceVisible == true') < repair.index('_calendar?.TryRepairVisibleSurface()'),
 'calendar repair only on explicit user action': 'if (plan.InspectCalendar)' in repair and 'TryRepairVisibleSurface()' in repair,
 'display repair does not alter calendar events or Google tokens':
   all(z not in repair for z in ('calendar-events.json', 'calendar-google-token.dat', 'File.Delete(', 'Directory.Delete(')),
 'failure warns rather than assuming success': '캘린더 표시 실패 또는 재시도 제한' in repair,
 'no infinite loop or unconditional retries': not any(z in repair for z in ('while (', 'Thread.Sleep(', 'Task.Delay(')),
 'original calendar-specific repair and standalone window remain': 
   'private void RepairCalendarSurface()' in app and 'OpenCalendarStandaloneWindow' in app,
 'case matrix compiled into existing harness': 'DisplaySurfaceRecoveryPlan.Create(' in harness and
   'Production/DisplaySurfaceRecoveryPlan.cs' in csproj,
 'v8.0.25 or later with state schema unchanged':
   tuple(map(int,re.search(r'<Version>(\d+\.\d+\.\d+)</Version>',read('src/CyanDeck/CyanDeck.csproj')).group(1).split('.'))) >= (8,0,25)
   and 'Current = 35' in read('src/CyanDeck/Services/StateSchema.cs')
}
for label,ok in checks.items():print(('PASS' if ok else 'FAIL')+': '+label)
print(f'RESULT {sum(checks.values())}/{len(checks)}')
if not all(checks.values()):raise SystemExit(1)
