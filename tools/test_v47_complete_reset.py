#!/usr/bin/env python3
"""Non-destructive static gate for the opt-in complete local profile reset."""
from pathlib import Path
import xml.etree.ElementTree as ET

r = Path(__file__).resolve().parents[1]
def read(p): return (r / p).read_text(encoding='utf-8-sig')
app = read('src/CyanDeck/App.xaml.cs')
ctl = read('src/CyanDeck/Services/AppController.cs')
svc = read('src/CyanDeck/Services/CompleteLocalResetService.cs')
xaml = read('src/CyanDeck/Views/SettingsWindow.xaml')
ui = read('src/CyanDeck/Views/SettingsWindow.xaml.cs')
tree = ET.fromstring(xaml)
normal = ctl.split('private bool ResetCyanDeckToDefaults()', 1)[1].split('private bool ResetAllLocalUserData()', 1)[0]
destructive = ctl.split('private bool ResetAllLocalUserData()', 1)[1].split('private void ReplaceState(', 1)[0]
checks = {
 'XAML reset section parsed': tree is not None,
 'preserving and destructive actions both visible': 'FactoryResetButton' in xaml and 'CompleteLocalResetButton' in xaml,
 'destructive UI uses independent click handler': 'Click="CompleteLocalReset_Click"' in xaml and 'private void CompleteLocalReset_Click(' in ui,
 'destructive button uses factory managed-policy restrictions': 'CompleteLocalResetButton.IsEnabled = FactoryResetButton.IsEnabled;' in ui,
 'normal factory reset still preserves OAuth client': 'defaults.Settings.GoogleCalendarClientId = _state.Settings.GoogleCalendarClientId;' in normal,
 'normal factory reset still uses verified backup': '_store.Import(backup);' in normal,
 'full reset requires two confirmations': 'MessageBoxButton.YesNo' in destructive and '"모든 데이터 삭제"' in destructive,
 'full reset checks managed capability and pending updater recovery': 'EnsureManagedCapability("ImportExport.Import"' in destructive and 'UpdateService.HasPendingRecovery()' in destructive,
 'old process creates ticket and then exits': 'CompleteLocalResetService.CreateRequest(DataRootPaths.ActiveRoaming)' in destructive and 'Exit();' in destructive,
 'new process verifies request before loading WPF desktop': app.index('CompleteLocalResetService.ExecutePending(') < app.index('RunStartupUpdatePreflightAsync('),
 'service only receives configured roaming data root': 'ExecutePending(DataRootPaths.ActiveRoaming, completeResetTicket)' in app,
 'one time marker refuses overwrite and flushes': 'FileMode.CreateNew' in svc and 'stream.Flush(flushToDisk: true)' in svc,
 'request expires within 15 minutes': 'TicketLifetime = TimeSpan.FromMinutes(15)' in svc,
 'source directory fixed to CyanDeck profile': '"CyanDeck", StringComparison.OrdinalIgnoreCase' in svc and 'CheckRoot(root);' in svc,
 'filesystem staging is performed only after verified ticket': svc.index('string.Equals(request.Ticket, ticket') < svc.index('Directory.Move(full, quarantine)'),
 'reject junctions and symlinks before deletion': 'RejectReparsePoints(root);' in svc and 'FileAttributes.ReparsePoint' in svc,
 'no Google API or desktop delete commands': all(x not in svc for x in ['SendApiAsync(', 'SHFileOperation(', 'RecycleBin(', 'HttpClient']),
 'cleanup fails closed': 'Shutdown();' in app.split('var completeResetTicket =',1)[1].split('// Do not create desktop group',1)[0],
 'reset explicitly skips automatic user backup creation': '_store.Export(' not in destructive and '_store.SaveRecoveryCheckpoint(' not in destructive,
 'main project upgraded to v8.0.23': '<Version>8.0.23</Version>' in read('src/CyanDeck/CyanDeck.csproj'),
}
for name, success in checks.items(): print(('PASS' if success else 'FAIL') + ': ' + name)
print(f'RESULT {sum(checks.values())}/{len(checks)}')
if not all(checks.values()): raise SystemExit(1)
