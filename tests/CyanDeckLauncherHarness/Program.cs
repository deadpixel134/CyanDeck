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
// Optional real-process contract. The PowerShell preview smoke suite passes
// a self-contained bridge and a disposable DesktopDeck.exe probe in that order.
// ProcessStartInfo.ArgumentList here tests real Windows quoting, not a shell's
// best-effort reconstruction of a command-line string.
if (args.Length != 0)
{
    Check(args.Length == 3, "real launch harness needs launcher, output and cwd paths");
    var launcherExe = Path.GetFullPath(args[0]);
    var outputPath = Path.GetFullPath(args[1]);
    var independentWorkingDir = Path.GetFullPath(args[2]);
    Check(File.Exists(launcherExe), "compiled CyanDeck preview exists");
    if (File.Exists(outputPath)) File.Delete(outputPath);
    var trickyArgs = new[]
    {
        "--managed-policy",
        @"C:\some folder\한글 정책 name with spaces.json",
        "--wait-for-pid", "1234", "--peek-toggle"
    };
    var start = new System.Diagnostics.ProcessStartInfo(launcherExe)
    {
        UseShellExecute = false,
        WorkingDirectory = independentWorkingDir
    };
    foreach (var argument in trickyArgs) start.ArgumentList.Add(argument);
    start.Environment["CYANDECK_LAUNCH_FIXTURE_OUTPUT"] = outputPath;
    using (var child = System.Diagnostics.Process.Start(start)
        ?? throw new InvalidOperationException("CyanDeck preview could not be launched"))
    {
        Check(child.WaitForExit(15000) && child.ExitCode == 0,
            "real preview successfully handed off to legacy probe");
    }
    var startedAt = System.Diagnostics.Stopwatch.StartNew();
    while (!File.Exists(outputPath) && startedAt.Elapsed < TimeSpan.FromSeconds(15))
        Thread.Sleep(100);
    Check(File.Exists(outputPath), "adjacent legacy probe received the process handoff");
    // The probe may still be flushing the JSON when File.Exists first succeeds.
    string[]? actual = null;
    while (actual is null && startedAt.Elapsed < TimeSpan.FromSeconds(15))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(outputPath));
            actual = doc.RootElement.GetProperty("Args").EnumerateArray()
                .Select(v => v.GetString() ?? "").ToArray();
            Check(doc.RootElement.GetProperty("ExecutableName").GetString() == "DesktopDeck.exe",
                "preview starts the sibling DesktopDeck.exe, not a different executable");
            Check(string.Equals(
                    Path.GetFullPath(doc.RootElement.GetProperty("WorkingDirectory").GetString()!),
                    Path.GetDirectoryName(launcherExe), StringComparison.OrdinalIgnoreCase),
                "real handoff uses sibling executable directory as working directory");
        }
        catch (IOException) { Thread.Sleep(100); }
        catch (System.Text.Json.JsonException) { Thread.Sleep(100); }
    }
    Check(actual is not null && actual.SequenceEqual(trickyArgs),
        "actual Windows handoff retains Unicode, spaces and argument boundaries");

    // Diagnostic mode checks the adjacent executable without launching it.
    File.Delete(outputPath);
    var preflight = new System.Diagnostics.ProcessStartInfo(launcherExe)
    {
        UseShellExecute = false,
        WorkingDirectory = independentWorkingDir
    };
    preflight.ArgumentList.Add(CyanDeckLaunchPolicy.DiagnosticOption);
    preflight.Environment["CYANDECK_LAUNCH_FIXTURE_OUTPUT"] = outputPath;
    using (var checkOnly = System.Diagnostics.Process.Start(preflight)
        ?? throw new InvalidOperationException("Read-only handoff diagnostic failed to launch"))
        Check(checkOnly.WaitForExit(15000) && checkOnly.ExitCode == 0,
            "read-only handoff diagnostic exits successfully");
    Thread.Sleep(300);
    Check(!File.Exists(outputPath), "handoff diagnostic does not launch DesktopDeck.exe");
}
Console.WriteLine("CyanDeckLauncherHarness: policy assertions passed.");
