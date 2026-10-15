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

    Console.WriteLine("PASS: full local data reset token, expired request, external file and disposable data");
}
finally
{
    if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
}
