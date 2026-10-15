#!/usr/bin/env python3
"""v8.0.33: OS session termination must never wait for slow shell/disk teardown."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
def source(path): return (root/path).read_text(encoding="utf-8-sig")
app=source("src/CyanDeck/App.xaml.cs")
ctl=source("src/CyanDeck/Services/AppController.cs")
policy=source("src/CyanDeck/Services/WindowsSessionExitPolicy.cs")
store=source("src/CyanDeck/Services/StateStore.cs")
harness=source("tests/CompleteResetHarness/Program.cs")
proj=source("tests/CompleteResetHarness/CompleteResetHarness.csproj")
normal=ctl.split("public void Dispose()",1)[1]
fast=ctl.split("public void DisposeForWindowsSessionEnd()",1)[1].split("public void Dispose()",1)[0]
query=app.split("protected override void OnSessionEnding(",1)[1].split("protected override void OnExit(",1)[0]
exit=app.split("protected override void OnExit(",1)[1]
checks={
 "WPF explicitly accepts logoff and shutdown requests":
    "e.Cancel = false;" in query and "base.OnSessionEnding(e);" in query,
 "session query callback performs no disk writes, windows close or blocking waits":
    all(x not in query for x in ("File.", "PersistNow(", "Dispose(", "Thread.Sleep(", "Wait(", "MessageBox.", "Close(", "Shutdown()")),
 "normal user-initiated tray exit still runs complete Dispose":
    "else" in exit and "_controller?.Dispose();" in exit,
 "Windows shutdown invokes dedicated nonblocking cleanup":
    "if (_windowsSessionEnding)" in exit and
    "_controller?.DisposeForWindowsSessionEnd();" in exit,
 "fast disposal does not attempt full layout save or spawn CMD/PowerShell":
    all(x not in fast for x in ("PersistNow(", "_store.Save(", "Process.Start(", "File.Replace(", "TryCommit", "WaitForExit(")),
 "fast disposal does not call native desktop icon restoration or close HWNDs":
    all(x not in fast for x in ("SetNativeDesktopIconsVisible(", "CloseFromController(", "_windows.ToArray()", "_host.", "_calendar?.Dispose()")),
 "fast path retains normal automatic per-session checkpoint semantics":
    "_store.EndSession();" in fast and "StartupHealthService.MarkCleanExit();" in fast,
 "no clean marker when user layout may be unsaved":
    "WindowsSessionExitPolicy.CanClearRecoveryMarker(" in fast and
    "_persistDirty" in fast and "_persistStorageRetryCount" in fast,
 "no clean marker during monitor/display/layout transitions":
    "_displayTransition?.IsStableForUserLayoutAction == false" in fast and
    "_monitorContentSwapInProgress || _geometryResetPendingRestart" in fast,
 "windows session policy denies abnormal/dirty/inflight exits":
    all(x in policy for x in ("!abnormalTermination","!hasUnsavedLayoutChanges",
      "pendingStorageRetries == 0","!displayTransitionInProgress","!layoutOperationInProgress")),
 "ordinary application exit keeps durable final forced checkpoint":
    "PersistNow(force: true, bypassDisplayTransition: true, requireDurableSave: true)" in normal,
 "existing lock retry fallback remains available for non-PC-shutdown saves":
    "TryCommitExistingLayoutViaCmd" in store and "File.Replace(" in store,
 "closed session still keeps old baseline if pending edits were not persisted":
    "session.active" in store and "startup-baseline.json" in store and
    "if (WindowsSessionExitPolicy.CanClearRecoveryMarker(" in fast,
 "Windows executable harness links the exact production policy":
    "WindowsSessionExitPolicy.cs" in proj and "Clean(false, true, 0, false, false)" in harness,
 "previous tray and calendar exit behavior stays unchanged":
    "private void Exit()" in ctl and
    "System.Windows.Application.Current.Shutdown();" in ctl,
 "version advanced with unchanged persisted schema":
    tuple(map(int,re.search(r"<Version>(\d+\.\d+\.\d+)</Version>",source("src/CyanDeck/CyanDeck.csproj")).group(1).split("."))) >= (8,0,33)
    and "Current = 35" in source("src/CyanDeck/Services/StateSchema.cs")
}
for label,ok in checks.items(): print(("PASS: " if ok else "FAIL: ")+label)
print(f"RESULT {sum(checks.values())}/{len(checks)}")
if not all(checks.values()): raise SystemExit(1)
