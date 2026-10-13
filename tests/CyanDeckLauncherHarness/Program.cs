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
    var missingRejected = false;
    try { CyanDeckLaunchPolicy.ResolveLegacyExecutable(scratch); }
    catch (FileNotFoundException) { missingRejected = true; }
    Check(missingRejected, "missing legacy EXE cannot fall back to system PATH");
    var legacy = Path.Combine(scratch, "DesktopDeck.exe");
    File.WriteAllText(legacy, "test-only ordinary file");
    Check(CyanDeckLaunchPolicy.ResolveLegacyExecutable(scratch) == legacy,
        "launcher resolves only its companion executable");
}
finally { Directory.Delete(scratch, recursive: true); }
Console.WriteLine("CyanDeckLauncherHarness: policy assertions passed.");
