using CyanDeck.Launcher;

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + label);
    Console.WriteLine("PASS " + label);
}
static bool Rejects(params string[] args)
{
    try { CyanDeckLaunchPolicy.ValidateArguments(args); return false; }
    catch (ArgumentException) { return true; }
    catch (InvalidOperationException) { return true; }
}
Check(CyanDeckLaunchPolicy.ValidateArguments([]).Count == 0, "empty args launch existing app");
Check(CyanDeckLaunchPolicy.ValidateArguments(["--peek-toggle", "--safe-mode"])
    .SequenceEqual(["--peek-toggle", "--safe-mode"]), "activation options forwarded");
Check(CyanDeckLaunchPolicy.ValidateArguments(["--recover-groups-at-cursor", "--ignore-crash-loop"])
    .Count == 2, "Explorer recovery shortcut forwarded");
Check(CyanDeckLaunchPolicy.ValidateArguments(["--managed-policy", @"C:\a folder\managed.json",
    "--wait-for-pid", "1234"]).SequenceEqual(["--managed-policy", @"C:\a folder\managed.json",
    "--wait-for-pid", "1234"]), "spaced policy path and valid PID are forwarded unchanged");
Check(Rejects("--activate-cyandeck") && Rejects("--ACTIVATE-CYANDECK"),
    "unsupported profile cutover rejected case-insensitively");
Check(Rejects("--unknown"), "unknown flags rejected rather than silently ignored");
Check(Rejects("--safe-mode", "--SAFE-MODE"), "duplicate arguments rejected");
Check(Rejects("--wait-for-pid", "0") && Rejects("--wait-for-pid", "garbage"),
    "invalid PIDs rejected");
Check(Rejects("--managed-policy") && Rejects("--managed-policy", "--safe-mode"),
    "option values cannot swallow other flags");
var scratch = Path.Combine(Path.GetTempPath(), "cyandeck-launch-policy-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var isolatedLocalRoot = Path.Combine(scratch, "local", "DesktopDeck");
    var missingRejected = false;
    try { CyanDeckLaunchPolicy.ResolveLegacyExecutable(scratch, isolatedLocalRoot); }
    catch (FileNotFoundException) { missingRejected = true; }
    Check(missingRejected, "missing legacy EXE cannot fall back to system PATH");
    var legacy = Path.Combine(scratch, "DesktopDeck.exe");
    File.WriteAllText(legacy, "test-only ordinary file");
    Check(CyanDeckLaunchPolicy.ResolveLegacyExecutable(scratch, isolatedLocalRoot) == legacy,
        "launcher resolves only its companion executable");
    Check(!Directory.Exists(isolatedLocalRoot), "read-only launcher check does not create local storage");

    var migration = Path.Combine(isolatedLocalRoot, "migration");
    Directory.CreateDirectory(migration);
    var fence = Path.Combine(migration, "legacy-writer-fenced.json");
    File.WriteAllText(fence, "{invalid");
    var malformedFenceRejected = false;
    try { CyanDeckLaunchPolicy.ResolveLegacyExecutable(scratch, isolatedLocalRoot); }
    catch (InvalidOperationException) { malformedFenceRejected = true; }
    Check(malformedFenceRejected, "malformed cutover fence blocks handoff");
    File.Delete(fence);
    Directory.CreateDirectory(fence);
    var directoryFenceRejected = false;
    try { CyanDeckLaunchPolicy.ResolveLegacyExecutable(scratch, isolatedLocalRoot); }
    catch (InvalidOperationException) { directoryFenceRejected = true; }
    Check(directoryFenceRejected, "directory-shaped cutover fence blocks handoff");
    Directory.Delete(fence);
    Check(CyanDeckLaunchPolicy.ResolveLegacyExecutable(scratch, isolatedLocalRoot) == legacy,
        "removing test cutover fence restores handoff");
}
finally { Directory.Delete(scratch, recursive: true); }
Console.WriteLine("CyanDeckLauncherHarness: policy assertions passed.");
