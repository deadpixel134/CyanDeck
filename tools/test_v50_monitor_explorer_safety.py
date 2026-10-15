#!/usr/bin/env python3
"""v8.0.26 non-destructive monitor/Explorer recovery checks, never launches WPF."""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1]
def read(path):
    return (root / path).read_text(encoding='utf-8-sig')
app = read('src/CyanDeck/Services/AppController.cs')
policy = read('src/CyanDeck/Layout/SavedMonitorLayoutSafetyPolicy.cs')
csproj = read('tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj')
harness = read('tests/FenceVisibilityHarness/Program.cs')
profile = app.split('private void CaptureMonitorProfile()', 1)[1].split('private int GetMaxPage()', 1)[0]
rehome = app.split('private bool TryRehomeGroupsAfterMonitorReduction(', 1)[1].split('private bool TryApplySavedMonitorPlacements(', 1)[0]
saved = app.split('private bool TryApplySavedMonitorPlacements(', 1)[1].split('private void ApplyInitialLayoutAdjustment()', 1)[0]
stable = app.split('private void RestoreStableDisplayLayout(', 1)[1].split('private void FlushDeferredDisplayLayoutSave()', 1)[0]
shell = app.split('private void StartShellRecovery()', 1)[1].split('private static TimeSpan GetUserIdleDuration()', 1)[0]
checks = {
 'monitor topology verified before stable restore side effects':
    stable.index('SavedMonitorLayoutSafetyPolicy.MatchesSettledTopology(') < stable.index('_displayTransitionStableRestoreCount++'),
 'stale monitor snapshot starts a fresh settle pass':
    '_displayTransition?.HandleWindowMessage(WpfDisplayTransitionBridge.WmDisplayChange);' in stable,
 'monitor profile guard requires nonempty and finite rectangles':
    'double.IsFinite(geometry.X)' in policy and 'geometry.Width <= 0' in policy and 'geometry.ExpandedHeight <= 0' in policy,
 'saved monitor placements validated before mutation':
    saved.index('SavedMonitorLayoutSafetyPolicy.IsUsable(') < saved.index('ApplyPlacements(selected);'),
 'matching target monitor profile uses checked restore':
    'TryApplySavedMonitorPlacements(targetProfile.Fences, "matching monitor profile")' in stable,
 'scaled monitor profile uses checked restore':
    'TryApplySavedMonitorPlacements(' in stable and '"scaled monitor profile"' in stable,
 'profile apply creates recovery checkpoint before mutation':
    saved.index('SaveRecoveryCheckpoint(') < saved.index('ApplyPlacements(selected);'),
 'profile apply checks nonempty checkpoint':
    'new FileInfo(checkpoint).Length == 0' in saved,
 'failed saved profile restore rolls back placements':
    'ApplyPlacements(previous);' in saved and
    '_monitorProfileRestoreBlockedSignature = _activeMonitorSignature;' in saved,
 'failed restore never overwrites previously saved monitor profile':
    '_monitorProfileRestoreBlockedSignature' in profile and
    'string.Equals(_monitorProfileRestoreBlockedSignature, _activeMonitorSignature' in profile,
 'auto rehome checks durable checkpoint before changing group':
    rehome.index('new FileInfo(checkpoint).Length == 0') < rehome.index('group.X = placement.X;'),
 'auto rehome checkpoint failure blocks unsafe fallback':
    'return true; // Handled as fail-closed' in rehome,
 'partial auto rehome failure rolls back':
    'beforeMutation = CapturePlacements();' in rehome and
    'ApplyPlacements(beforeMutation);' in rehome,
 'auto rehome exception blocks unsafe fallback':
    'return true; // Intentionally suppress the unsafe fallback.' in rehome,
 'Explorer heartbeat pauses visual repairs during transition':
    'var displayTransitionInProgress = _displayTransition?.IsStableForUserLayoutAction == false;' in shell
    and '!displayTransitionInProgress' in shell,
 'Explorer shell restack still requires destroyed previous host':
    'if (!previousShellWasValid)' in shell and
    '_calendar?.NotifyExplorerShellRebuilt();' in shell,
 'native icon visibility refresh preserved':
    'StartNativeIconDisplayRepairChecks();' in stable,
 'all monitor policy tests compiled in existing Windows harness':
    'Production/SavedMonitorLayoutSafetyPolicy.cs' in csproj and
    'SavedMonitorLayoutSafetyPolicy.MatchesSettledTopology' in harness,
 'no delete or move APIs introduced to monitor restore':
    all(x not in saved + rehome for x in ('File.Delete(', 'Directory.Delete(', 'File.Move(', 'Directory.Move(')),
 'v8.0.26 or later, schema remains 35':
    tuple(map(int,re.search(r'<Version>(\d+\.\d+\.\d+)</Version>',read('src/CyanDeck/CyanDeck.csproj')).group(1).split('.'))) >= (8,0,26)
    and 'Current = 35' in read('src/CyanDeck/Services/StateSchema.cs')
}
for label,ok in checks.items():
    print(('PASS' if ok else 'FAIL')+': '+label)
print('RESULT',sum(checks.values()),'/',len(checks))
if not all(checks.values()):
    raise SystemExit(1)
