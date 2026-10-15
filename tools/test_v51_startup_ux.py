#!/usr/bin/env python3
"""v8.0.27 non-destructive WPF startup/onboarding regression assertions."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
def read(p): return (root/p).read_text(encoding='utf-8-sig')
app=read('src/CyanDeck/App.xaml.cs')
presentation=read('src/CyanDeck/Services/StartupUpdatePresentationPolicy.cs')
wizard=read('src/CyanDeck/Views/FirstRunWizardWindow.cs')
onboarding=read('src/CyanDeck/Services/FirstRunOnboardingPolicy.cs')
harness=read('tests/FenceVisibilityHarness/Program.cs')
resetHarness=read('tests/CompleteResetHarness/Program.cs')
early=app.split('private async Task<(bool ExitForUpdate, bool Attempted)> RunStartupUpdatePreflightAsync(',1)[1].split('private static string? GetOptionValue(',1)[0]
checks={
 'first run wizard precedes desktop app controller':
    app.index('wizard.ShowDialog()') < app.index('_controller = new AppController('),
 'first run wizard remains before auto update preflight':
    app.index('var firstRun = FirstRunOnboardingPolicy.ShouldOffer') < app.index('var preflight = firstRun ?'),
 'updates bypass safe/read-only/recovery/external commands':
    'StartupUpdatePolicy.ShouldPreflight(' in early,
 'fast check does not show immediate progress window':
    early.index('var checkTask = UpdateService.CheckAsync(') < early.index('ShowStartupStatus("새 버전을 확인하고 있습니다.'),
 'grace uses race with actual manifest query':
    'Task.WhenAny(checkTask, graceTask)' in early and
    'StartupUpdatePresentationPolicy.ShouldShowChecking(' in early,
 'slow check shows progress before desktop initialization':
    early.index('ShowStartupStatus("새 버전을 확인하고 있습니다.') < early.index('var check = await checkTask;') and
    app.index('var preflight = firstRun ?') < app.index('_controller.Start();'),
 'known update shows status even if manifest was fast':
    'ShowStartupStatus($"CyanDeck {check.LatestVersion.ToString(3)}' in early,
 'status skip button cancels network and download':
    'splash.SkipRequested += () =>' in early and 'skip.Cancel();' in early and
    'UpdateService.StagePackageAsync(check, skip.Token)' in early,
 'manifest timeout preserved':
    'checkTimeout.CancelAfter(TimeSpan.FromSeconds(8))' in early,
 'installer handoff retains readiness event and live-process guard':
    'readySignal.WaitOne(TimeSpan.FromSeconds(8))' in early and
    'StartupUpdatePolicy.ShouldHandOffToInstaller(installerReady, !installer.HasExited)' in early,
 'status closed before installer takes over':
    early.index('splash.Close();') < early.index('Shutdown();'),
 'minimum status dwell avoids same-frame flash':
    'Stopwatch.GetElapsedTime' not in early or
    'GetElapsedTime(splashShownAt).TotalMilliseconds' in early,
 'skip cancels minimum dwell':
    'RemainingVisibleMilliseconds(' in early and
    'elapsed, userRequestedSkip' in early,
 'policy exposes grace and minimum thresholds':
    'CheckingGraceMilliseconds = 750' in presentation and
    'MinimumVisibleMilliseconds = 950' in presentation,
 'status does not mutate Desktop/Google data':
    all(v not in early for v in ('File.Delete(', 'Directory.Delete(', 'DeleteEventAsync(', 'DeleteRemote')),
 'first run has explicit cancel button and explanatory copy':
    'buttons.Children.Add(_cancelButton)' in wizard and
    '_cancelButton.Click += (_, _) => Close()' in wizard,
 'onboarding re-open distinguished from first-run':
    '(_firstRun ? "저장 후 시작" : "변경 적용")' in wizard and
    '_cancelButton.Content = _firstRun ? "설정하지 않고 종료" : "변경 취소"' in wizard,
 'first run never alters Explorer until configuration saved':
    'firstStore.Save(new DeckState { Settings = configured });' in app and
    app.index('firstStore.Save(new DeckState { Settings = configured });') < app.index('_controller.Start();'),
 'onboarding preserves existing layout and recovery':
    'layout.json.bak' in onboarding and 'calendar-google-token.dat' in onboarding and
    'Directory.EnumerateFileSystemEntries(recovery).Any()' in onboarding,
 'startup UI tests compiled in Windows harness':
    'StartupUpdatePresentationPolicy.ShouldShowChecking(' in harness and
    'Production/StartupUpdatePresentationPolicy.cs' in read('tests/FenceVisibilityHarness/FenceVisibilityHarness.csproj'),
 'first run protection tests compiled in disposable harness':
    'FirstRunOnboardingPolicy.ShouldOffer(' in resetHarness and
    'Production/FirstRunOnboardingPolicy.cs' in read('tests/CompleteResetHarness/CompleteResetHarness.csproj'),
 'v8.0.27 or later without schema migration':
    tuple(map(int,re.search(r'<Version>(\d+\.\d+\.\d+)</Version>',read('src/CyanDeck/CyanDeck.csproj')).group(1).split('.'))) >= (8,0,27)
    and 'Current = 35' in read('src/CyanDeck/Services/StateSchema.cs')
}
for key,value in checks.items():print(('PASS' if value else 'FAIL')+': '+key)
print('RESULT',sum(checks.values()),'/',len(checks))
if not all(checks.values()):raise SystemExit(1)
