using CyanDeck.Services;

// All filesystem tests use disposable test directories, NEVER real user state.
var parent = Path.Combine(Path.GetTempPath(),
    "CyanDeck-full-reset-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(parent);
try
{
    var outside = Path.Combine(parent, "Desktop-original.txt");
    File.WriteAllText(outside, "untouched");
    var root = Path.Combine(parent, "CyanDeck");
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "layout.json"), "{\"groups\":[1]}");
    File.WriteAllText(Path.Combine(root, "calendar-events.json"), "[{\"title\":\"local\"}]");
    File.WriteAllText(Path.Combine(root, "calendar-google-token.dat"), "encrypted-token-mock");
    Directory.CreateDirectory(Path.Combine(root, "recovery"));
    File.WriteAllText(Path.Combine(root, "recovery", "snapshot.json"), "backup");

    var ticket = CompleteLocalResetService.CreateRequest(root);
    if (ticket.Length != 32 || !File.Exists(Path.Combine(root, CompleteLocalResetService.RequestFileName)))
        throw new Exception("Durable request not created.");
    try
    {
        CompleteLocalResetService.ExecutePending(root, new string('a', 32));
        throw new Exception("Invalid authorization was accepted.");
    }
    catch (InvalidDataException) { }
    if (!File.Exists(Path.Combine(root, "calendar-google-token.dat")))
        throw new Exception("Unverified request mutated user data.");

    CompleteLocalResetService.ExecutePending(root, ticket);
    if (Directory.Exists(root))
        throw new Exception("Confirmed local profile was not removed.");
    if (File.ReadAllText(outside) != "untouched")
        throw new Exception("Unrelated user file was changed.");

    Directory.CreateDirectory(root);
    var cancelled = CompleteLocalResetService.CreateRequest(root);
    CompleteLocalResetService.CancelRequest(root, cancelled);
    if (File.Exists(Path.Combine(root, CompleteLocalResetService.RequestFileName)))
        throw new Exception("Failed launch request was not canceled.");

    var stale = CompleteLocalResetService.CreateRequest(root);
    var requestPath = Path.Combine(root, CompleteLocalResetService.RequestFileName);
    File.WriteAllText(requestPath,
        "{\"Ticket\":\"" + stale + "\",\"IssuedUtc\":\"2020-01-01T00:00:00Z\"}");
    try
    {
        CompleteLocalResetService.ExecutePending(root, stale);
        throw new Exception("Expired request was accepted.");
    }
    catch (InvalidDataException) { }
    if (!Directory.Exists(root))
        throw new Exception("Expired request modified data.");

    try
    {
        CompleteLocalResetService.ExecutePending(parent, stale);
        throw new Exception("Non-CyanDeck directory was accepted.");
    }
    catch (InvalidDataException) { }
    // The interrupted onboarding marker permits a retry, but existing main
    // layouts and recovery data must never be overwritten by the wizard.
    var onboardingRoot = Path.Combine(parent, "first-run-test");
    if (!FirstRunOnboardingPolicy.ShouldOffer(onboardingRoot))
        throw new Exception("Brand-new onboarding was not offered.");
    Directory.CreateDirectory(onboardingRoot);
    File.WriteAllText(Path.Combine(onboardingRoot, FirstRunOnboardingPolicy.PendingMarker), "pending");
    File.WriteAllText(Path.Combine(onboardingRoot, "calendar-google-token.dat"), "mock");
    if (!FirstRunOnboardingPolicy.ShouldOffer(onboardingRoot))
        throw new Exception("Interrupted initial OAuth setup cannot resume.");
    if (FirstRunOnboardingPolicy.ShouldOffer(onboardingRoot, safeMode: true) ||
        FirstRunOnboardingPolicy.ShouldOffer(onboardingRoot, externalCommand: true) ||
        FirstRunOnboardingPolicy.ShouldOffer(onboardingRoot, updateRecoveryPending: true))
        throw new Exception("Protected startup unexpectedly offered the first-run wizard.");
    File.WriteAllText(Path.Combine(onboardingRoot, "layout.json"), "{ }");
    if (FirstRunOnboardingPolicy.ShouldOffer(onboardingRoot))
        throw new Exception("Durably saved user layout was offered for overwriting.");
    File.Delete(Path.Combine(onboardingRoot, "layout.json"));
    File.Delete(Path.Combine(onboardingRoot, FirstRunOnboardingPolicy.PendingMarker));
    if (FirstRunOnboardingPolicy.ShouldOffer(onboardingRoot))
        throw new Exception("Existing OAuth state was treated as a new installation.");
    Console.WriteLine("PASS: first-run setup retry, OAuth preservation and state-overwrite guards");

    // Exact signed-package digest cooldown uses only the disposable local
    // test directory. It never touches actual %LOCALAPPDATA% or user profiles.
    var mockLocal = Path.Combine(parent, "local");
    var sha = new string('a', 64);
    var differentSha = new string('b', 64);
    var failureMarker = FailedUpdateRetryPolicy.GetMarkerPath(mockLocal, sha.ToUpperInvariant());
    if (!failureMarker.EndsWith("failed-" + sha + ".txt", StringComparison.Ordinal))
        throw new Exception("Failed package marker is not normalized by SHA-256.");
    if (FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(mockLocal, sha, DateTime.UtcNow))
        throw new Exception("Missing failure marker blocked startup.");
    Directory.CreateDirectory(Path.GetDirectoryName(failureMarker)!);
    File.WriteAllText(failureMarker, "simulated installer error only");
    var nowUtc = DateTime.UtcNow;
    File.SetLastWriteTimeUtc(failureMarker, nowUtc.AddHours(-1));
    if (!FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(mockLocal, sha, nowUtc))
        throw new Exception("Recent failed digest was not deferred.");
    if (FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(mockLocal, differentSha, nowUtc))
        throw new Exception("One failed digest blocked a corrected release.");
    File.SetLastWriteTimeUtc(failureMarker, nowUtc.AddHours(-13));
    if (FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(mockLocal, sha, nowUtc))
        throw new Exception("Expired failure marker prevented the 12-hour retry.");
    File.SetLastWriteTimeUtc(failureMarker, nowUtc.AddHours(2));
    if (FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(mockLocal, sha, nowUtc))
        throw new Exception("Future-dated corrupt failure marker blocked startup.");
    if (FailedUpdateRetryPolicy.IsAutomaticRetryDeferred(mockLocal, "broken", nowUtc))
        throw new Exception("Malformed package digest unexpectedly blocked startup.");
    File.Delete(failureMarker);
    Console.WriteLine("PASS: exact digest failure cooldown, expiry, corrected release and manual retry isolation");

    var diagnostic = UpdateFailureDiagnostics.Inspect(mockLocal, nowUtc);
    if (diagnostic.HasLog || diagnostic.RecentFailedPackages != 0)
        throw new Exception("No failure history should mean no diagnostics.");
    File.WriteAllText(failureMarker, "failed");
    File.SetLastWriteTimeUtc(failureMarker, nowUtc.AddHours(-2));
    var logPath = UpdateFailureDiagnostics.LogPath(mockLocal);
    File.WriteAllText(logPath,
        "[2026-10-15T08:00:00Z] Update failed: sharing violation: C:\\\\private\\\\file\\\\token.dat");
    diagnostic = UpdateFailureDiagnostics.Inspect(mockLocal, nowUtc);
    if (!diagnostic.HasLog || diagnostic.RecentFailedPackages != 1 ||
        !diagnostic.Text.Contains("파일 사용 중") || diagnostic.Text.Contains("private") ||
        diagnostic.Text.Contains("token.dat"))
        throw new Exception("Update failure summary must classify safely without leaking file paths.");
    File.SetLastWriteTimeUtc(failureMarker, nowUtc.AddHours(-14));
    diagnostic = UpdateFailureDiagnostics.Inspect(mockLocal, nowUtc);
    if (diagnostic.RecentFailedPackages != 0)
        throw new Exception("Expired automatic update retry hold still appears active.");
    Console.WriteLine("PASS: bounded update failure diagnostic categories and retry state");

    // PC shutdown/logoff must neither block on heavy checkpoint retries nor
    // declare unsaved or transient monitor layouts clean. Pure policy tests do
    // not manipulate Windows sessions or any actual user files.
    static bool Clean(bool abnormal, bool dirty, int retries, bool displayChange, bool layoutChange)
        => WindowsSessionExitPolicy.CanClearRecoveryMarker(
            abnormal, dirty, retries, displayChange, layoutChange);
    if (!Clean(false, false, 0, false, false))
        throw new Exception("Previously autosaved stable session was not recognized as clean.");
    if (Clean(true, false, 0, false, false) ||
        Clean(false, true, 0, false, false) ||
        Clean(false, false, 1, false, false) ||
        Clean(false, false, 0, true, false) ||
        Clean(false, false, 0, false, true))
        throw new Exception("Unsaved, abnormal, locked or transitioning layout cleared its recovery marker.");
    Console.WriteLine("PASS: Windows fast session exit preserves recovery on dirty/saving/transitioning layouts");

    // v8.0.44: after "마지막 앱 업데이트 되돌리기" the rolled-back version is held from
    // AUTOMATIC reinstall until a newer release is published or the user updates manually.
    var v41 = new Version(8, 0, 41);
    var v42 = new Version(8, 0, 42);
    var v43 = new Version(8, 0, 43);
    if (!UpdateRollbackHold.IsAutomaticInstallHeld(v42, new Version(8, 0, 42, 0)) ||
        UpdateRollbackHold.IsAutomaticInstallHeld(v42, v43) ||
        UpdateRollbackHold.IsAutomaticInstallHeld(null, v42) ||
        !UpdateRollbackHold.ShouldReleaseHold(v42, v43) ||
        !UpdateRollbackHold.IsRollbackUndo(v42, new Version(8, 0, 41, 0)) ||
        UpdateRollbackHold.IsRollbackUndo(v42, v42) ||
        !UpdateRollbackHold.IsStale(v42, v42) || UpdateRollbackHold.IsStale(v42, v41))
        throw new Exception("Rollback hold decisions mismatch.");
    if (!UpdateRollbackHold.Record(mockLocal, new Version(8, 0, 42, 0)) || UpdateRollbackHold.ReadHeldVersion(mockLocal) != v42)
        throw new Exception("Rollback hold was not recorded.");
    if (!UpdateRollbackHold.ShouldSkipAutomaticInstall(mockLocal, new Version(8, 0, 42, 0), out var heldNow) || heldNow != v42)
        throw new Exception("The rolled-back version would be reinstalled automatically at the next start.");
    if (UpdateRollbackHold.DescribeRollbackCommand(mockLocal, v41) != "되돌리기 취소 (v8.0.42 다시 설치)…" ||
        UpdateRollbackHold.DescribeHold(mockLocal, v41) != "v8.0.42 자동 설치 보류 중" ||
        UpdateRollbackHold.DescribeHold(mockLocal, v42) != "")
        throw new Exception("Rollback hold wording mismatch.");
    if (UpdateRollbackHold.ShouldSkipAutomaticInstall(mockLocal, v43, out _) || File.Exists(UpdateRollbackHold.HoldPath(mockLocal)))
        throw new Exception("A newer release must release the rollback hold.");
    if (UpdateRollbackHold.DescribeRollbackCommand(mockLocal, v41) != "마지막 앱 업데이트 되돌리기…")
        throw new Exception("Rollback command label must return to normal once the hold is released.");
    Console.WriteLine("PASS: rolled-back version is not reinstalled automatically; newer release releases the hold");

    // v8.0.44: update storage keeps the rollback target plus one newer snapshot, removes
    // installed packages and old installer scripts, and never runs with a pending journal.
    var updatesRoot = Path.Combine(mockLocal, "updates");
    var rollbackRoot = Path.Combine(updatesRoot, "rollback");
    string MakeRollbackSnapshot(string name, DateTime createdUtc)
    {
        var dir = Path.Combine(rollbackRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CyanDeck.exe"), "exe");
        Directory.SetCreationTimeUtc(dir, createdUtc);
        return dir;
    }
    var rollbackTarget = MakeRollbackSnapshot("before-update-" + new string('1', 32), nowUtc.AddDays(-5));
    var newestSpare = MakeRollbackSnapshot("before-update-" + new string('2', 32), nowUtc.AddDays(-1));
    var olderSpare = MakeRollbackSnapshot("before-update-" + new string('3', 32), nowUtc.AddDays(-2));
    var oldestSpare = MakeRollbackSnapshot("pre-rollback-" + new string('4', 32), nowUtc.AddDays(-3));
    var foreignFolder = Path.Combine(rollbackRoot, "user-folder");
    Directory.CreateDirectory(foreignFolder);
    File.WriteAllText(Path.Combine(updatesRoot, "last-rollback.txt"), rollbackTarget);
    var stagedRoot = Path.Combine(updatesRoot, "staged");
    Directory.CreateDirectory(stagedRoot);
    foreach (var stagedVersion in new[] { "8.0.41.0", "8.0.42.0", "8.0.43.0", "8.0.44.0" })
        File.WriteAllText(Path.Combine(stagedRoot, "CyanDeck-" + stagedVersion + "-win-x64.zip"), "zip");
    var bootstrapRoot = Path.Combine(updatesRoot, "bootstrap");
    Directory.CreateDirectory(bootstrapRoot);
    var oldScript = Path.Combine(bootstrapRoot, "apply-update-" + new string('a', 32) + ".ps1");
    var freshScript = Path.Combine(bootstrapRoot, "apply-update-" + new string('b', 32) + ".ps1");
    File.WriteAllText(oldScript, "old");
    File.WriteAllText(freshScript, "new");
    File.SetLastWriteTimeUtc(oldScript, nowUtc.AddDays(-2));

    var pendingJournal = Path.Combine(updatesRoot, "pending-" + new string('c', 64) + ".json");
    File.WriteAllText(pendingJournal, "{}");
    var blocked = UpdateStorageMaintenance.Prune(mockLocal, new Version(8, 0, 42, 0), nowUtc);
    if (!blocked.Skipped || !Directory.Exists(olderSpare) || !File.Exists(oldScript))
        throw new Exception("Update storage was pruned while a recovery journal was pending.");
    File.Delete(pendingJournal);

    var pruned = UpdateStorageMaintenance.Prune(mockLocal, new Version(8, 0, 42, 0), nowUtc);
    if (pruned.DeletedRollbackSnapshots != 2 || !Directory.Exists(rollbackTarget) || !Directory.Exists(newestSpare) ||
        Directory.Exists(olderSpare) || Directory.Exists(oldestSpare) || !Directory.Exists(foreignFolder))
        throw new Exception("Rollback snapshot pruning must keep the rollback target and one newer snapshot only.");
    if (pruned.DeletedPackages != 3 || !File.Exists(Path.Combine(stagedRoot, "CyanDeck-8.0.44.0-win-x64.zip")) ||
        File.Exists(Path.Combine(stagedRoot, "CyanDeck-8.0.42.0-win-x64.zip")))
        throw new Exception("Installed packages must be removed and only the newest pending package kept.");
    if (pruned.DeletedScripts != 1 || File.Exists(oldScript) || !File.Exists(freshScript))
        throw new Exception("Only installer scripts older than a day may be removed.");
    var usageAfter = UpdateStorageMaintenance.Measure(mockLocal);
    if (usageAfter.RollbackSnapshots != 2 || usageAfter.StagedPackages != 1 || usageAfter.TotalBytes <= 0)
        throw new Exception("Update storage usage mismatch after pruning.");
    Console.WriteLine("PASS: update backup/download housekeeping keeps rollback target and skips pending recovery");


    Console.WriteLine("PASS: full local data reset token, expired request, external file and disposable data");
}
finally
{
    if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
}
