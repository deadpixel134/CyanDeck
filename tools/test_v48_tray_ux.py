#!/usr/bin/env python3
"""Non-destructive tray UX static regression. Does not launch the shell."""
from pathlib import Path
import xml.etree.ElementTree as ET
r=Path(__file__).resolve().parents[1]
def read(p): return (r/p).read_text(encoding='utf-8-sig')
app=read('src/CyanDeck/Services/AppController.cs')
routing=read('src/CyanDeck/Services/TrayMenuRoutingPolicy.cs')
ui=read('src/CyanDeck/Views/SettingsWindow.xaml')
harness=read('tests/FenceVisibilityHarness/Program.cs')
xml=ET.fromstring(ui)
menu=app.split('private void CompactTrayMenu(',1)[1].split('private void UpdateTrayText()',1)[0]
checks={
 'full/disabled/safe mode still compact same tray menu': app.count('CompactTrayMenu(menu);')==3,
 'primary settings and dynamic page entry remain': 'menu.Items.Add("설정…"' in app and 'pages.Tag = "desktop.pages"' in app,
 'first run guide present in normal and disabled variants': app.count('menu.Items.Add("시작 가이드 다시 보기…", null')==2,
 'menu version comes from assembly not obsolete Calendar v45': 'Assembly.GetExecutingAssembly().GetName().Version' in app and 'CyanDeck · Calendar v45' not in app,
 'stable command tags assigned before grouping': all(x in app for x in [
   'Tag = "brand.identity"', 'Tag = "calendar.status"', 'Tag = "calendar.enable"',
   'Tag = "group.enable"', 'Tag = "group.snapshots"', 'Tag = "recovery.backup"',
   'pages.Tag = "desktop.pages"', 'quickItem.Tag = "desktop.quick-hide"',
   'peekCommand.Tag = "desktop.peek"', 'nativeIcons.Tag = "desktop.native-icons"']),
 'menu uses pure stable ID routing': 'TrayMenuRoutingPolicy.Route(item.Tag as string, item.Text)' in menu,
 'original menu items detached and reused, not copied': 'menu.Items.Remove(item)' in menu and 'menu.Items.Clear()' not in menu,
 'calendar/group/recovery/tool submenus remain': all(x in menu for x in (
   'var calendar = new Forms.ToolStripMenuItem("캘린더")',
   'var groups = new Forms.ToolStripMenuItem("그룹 및 화면")',
   'var recovery = new Forms.ToolStripMenuItem("백업 및 복구")',
   'var tools = new Forms.ToolStripMenuItem("도구 및 도움말")')),
 'icon classification actions are nested inside group submenu': 'var iconTools = new Forms.ToolStripMenuItem("아이콘 및 분류")' in menu and
     'groups.DropDownItems.Add(iconTools)' in menu,
 'commands explicitly routed to SettingsOnly': 'case TrayMenuSection.SettingsOnly:' in menu and 'item.Dispose();' in menu,
 'unknown commands are kept in groups': 'return TrayMenuSection.Groups;' in routing,
 'status and native icon commands handled by ID regardless of text': 'case "calendar.status":' in routing and 'case "desktop.native-icons":' in routing,
 'safe mode diagnostics still available in settings': all(x in ui for x in (
   'Click="OpenDiagnosticLogs_Click"', 'Click="CreateDiagnosticBundle_Click"',
   'Click="OpenSettingsFolder_Click"', 'Click="CheckForUpdates_Click"')),
 'Windows startup setting remains in General settings': 'x:Name="StartupCheck"' in ui,
 'original reset controls preserved': 'x:Name="CompleteLocalResetButton"' in ui and
     'x:Name="FactoryResetButton"' in ui,
 'routing is exercised by compiled domain harness': 'trayCases' in harness and
     'TrayMenuRoutingPolicy.Route(id, label)' in harness,
 'tray UX release or later; schema unchanged': tuple(map(int, __import__('re').search(r'<Version>(\d+\.\d+\.\d+)</Version>', read('src/CyanDeck/CyanDeck.csproj')).group(1).split('.'))) >= (8, 0, 24) and
     'Current = 35' in read('src/CyanDeck/Services/StateSchema.cs'),
}
for k,v in checks.items(): print(('PASS' if v else 'FAIL')+': '+k)
print(f'RESULT {sum(checks.values())}/{len(checks)}')
if not all(checks.values()): raise SystemExit(1)
