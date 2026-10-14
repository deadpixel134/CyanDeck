using CyanDeck.Appearance;
using CyanDeck.Services;

var count = 0;
// Every combination of the three independent visibility toggles and the
// same-page/show-all flags, including both values of the quick-hide target mode.
foreach (var samePage in new[] { false, true })
foreach (var showOnAll in new[] { false, true })
foreach (var alwaysVisible in new[] { false, true })
foreach (var quickHidden in new[] { false, true })
foreach (var quickHideEnabled in new[] { false, true })
foreach (var ungroupedOnly in new[] { false, true })
{
    var page = samePage ? 2 : 1;
    const int activePage = 2;
    var expected = (showOnAll || samePage)
        && (!quickHidden || !quickHideEnabled || ungroupedOnly || alwaysVisible);
    var actual = FenceVisibilityPolicy.ShouldShow(
        page, showOnAll, alwaysVisible, activePage, quickHidden,
        quickHideEnabled, ungroupedOnly);
    if (expected != actual)
        throw new Exception($"Fence visibility mismatch: page={page} same={samePage} all={showOnAll} always={alwaysVisible} hidden={quickHidden} enabled={quickHideEnabled} iconsOnly={ungroupedOnly}");
    count++;
}
Console.WriteLine($"PASS: {count} fence visibility combinations");

// Deterministic Show Desktop visibility policy tests: no WPF or Win32 dependency.
var nativeRecoveryScenarios = 0;
foreach (var intended in new[] { false, true })
foreach (var wpfVisible in new[] { false, true })
foreach (var wpfMinimized in new[] { false, true })
foreach (var nativeVisible in new[] { false, true })
foreach (var nativeMinimized in new[] { false, true })
foreach (var desktopForeground in new[] { false, true })
foreach (var attached in new[] { false, true })
{
    var expected = intended && (!wpfVisible || wpfMinimized || !nativeVisible
        || nativeMinimized || (desktopForeground && !attached));
    var result = DesktopShowDesktopRecoveryPolicy.NeedsRecovery(intended, wpfVisible,
        wpfMinimized, nativeVisible, nativeMinimized, desktopForeground, attached);
    if (result != expected) throw new Exception("Show Desktop recovery policy mismatch");
    nativeRecoveryScenarios++;
}
Console.WriteLine($"PASS: {nativeRecoveryScenarios} Show Desktop visibility combinations");

// Startup preflight must prevent desktop flash only for safe, opted-in
// automatic installations. Explicit commands and failure-recovery bypass it.
var startupCases = 0;
foreach (var checkEnabled in new[] { false, true })
foreach (var autoInstall in new[] { false, true })
foreach (var safeMode in new[] { false, true })
foreach (var readOnly in new[] { false, true })
foreach (var recovery in new[] { false, true })
foreach (var external in new[] { false, true })
{
    var expected = checkEnabled && autoInstall && !safeMode && !readOnly && !recovery && !external;
    if (StartupUpdatePolicy.ShouldPreflight(checkEnabled, autoInstall, safeMode, readOnly,
        recovery, external) != expected)
        throw new Exception("Startup update preflight policy mismatch");
    startupCases++;
}
Console.WriteLine($"PASS: {startupCases} startup update preflight safety combinations");

// Never dismiss the startup status window until the independent updater has
// acknowledged its GUI is shown and the updater process is still alive.
foreach (var ready in new[] { false, true })
foreach (var running in new[] { false, true })
{
    if (StartupUpdatePolicy.ShouldHandOffToInstaller(ready, running) != (ready && running))
        throw new Exception("Startup updater window handoff decision mismatch");
}
Console.WriteLine("PASS: updater progress-window handoff safety combinations");

// Regression: Explorer WorkerW can obscure all groups without minimizing or
// hiding a single HWND. Older builds attempted one repair and then permanently
// suppressed retries until the desktop became healthy on its own.
var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
var last = now.AddMinutes(-3);
static bool ShouldRepair(bool missing, bool hidden, int count, DateTime now, DateTime last, bool queued = false)
    => DesktopShowDesktopRecoveryPolicy.ShouldSchedulePersistentRepair(missing, hidden, count, now, last, queued);
if (ShouldRepair(false, false, 10, now, last) ||
    ShouldRepair(true, false, 1, now, last) ||
    ShouldRepair(true, false, 2, now, last))
    throw new Exception("Healthy or transient desktop-band probes must never trigger a Z-order change.");
if (!ShouldRepair(true, false, 3, now, last))
    throw new Exception("Three persistent displaced-but-visible probes must permit a repair.");
if (ShouldRepair(true, false, 10, now, now.AddSeconds(-29)) ||
    ShouldRepair(true, false, 10, now, last, queued: true))
    throw new Exception("Persistent repairs must respect the 30-second throttle and pending timer.");
if (!ShouldRepair(true, false, 10, now.AddSeconds(31), now))
    throw new Exception("Still missing after one repair must be retried after the cooldown.");
if (!ShouldRepair(true, true, 1, now, last))
    throw new Exception("Native hidden/minimized HWND should be repaired without the displacement debounce.");
if (ShouldRepair(false, true, 20, now, last))
    throw new Exception("Intentionally hidden quick-hide/page surfaces must never be auto-restored.");
Console.WriteLine("PASS: persistent displaced-window recovery debounce and retry safety");



// Standalone icons and the Recycle Bin have independently configurable opacity.
// No WPF or native HWND is needed to test this decision policy.
var opacityScenarios = 0;
foreach (var enabled in new[] { false, true })
foreach (var recycleEnabled in new[] { false, true })
foreach (var isRecycle in new[] { false, true })
foreach (var interacting in new[] { false, true })
foreach (var legacy in new[] { 0.38d, 1d })
{
    var policy = new LooseIconOpacityOptions(enabled, .35d, .9d, recycleEnabled, legacy);
    var expected = enabled && (!isRecycle || recycleEnabled)
        ? (interacting ? .9d : .35d)
        : isRecycle && !interacting ? legacy : 1d;
    var actual = policy.Resolve(isRecycle, interacting);
    if (Math.Abs(actual - expected) > .0001d)
        throw new Exception($"Standalone opacity mismatch: enabled={enabled}, recycleEnabled={recycleEnabled}, recycle={isRecycle}, interacting={interacting}, expected={expected}, actual={actual}");
    opacityScenarios++;
}
var safety = new LooseIconOpacityOptions(true, double.NaN, double.PositiveInfinity, true, -.2);
if (safety.Resolve(false, false) != 1d || safety.Resolve(false, true) != 1d)
    throw new Exception("Invalid standalone opacity must return a safe value.");
if (new LooseIconOpacityOptions(true, 0d, 7d, true, .38d).Resolve(false, false) != .15d)
    throw new Exception("Standalone idle opacity minimum clamp failed.");
if (new LooseIconOpacityOptions(true, 0d, 7d, true, .38d).Resolve(false, true) != 1d)
    throw new Exception("Standalone hover opacity maximum clamp failed.");
Console.WriteLine($"PASS: {opacityScenarios} standalone icon opacity scenarios and safety cases");

// Tab switching stays click-driven unless explicitly opted into pointer hover.
var tabSwitchScenarios = 0;
foreach (var hoverEnabled in new[] { false, true })
foreach (var alreadySelected in new[] { false, true })
foreach (var leftPressed in new[] { false, true })
foreach (var dragging in new[] { false, true })
{
    var expected = hoverEnabled && !alreadySelected && !leftPressed && !dragging;
    var actual = TabSwitchActivationPolicy.ShouldSelectOnPointerEnter(hoverEnabled,
        alreadySelected, leftPressed, dragging);
    if (actual != expected)
        throw new Exception($"Tab hover mismatch: hover={hoverEnabled}, active={alreadySelected}, pressed={leftPressed}, dragging={dragging}");
    tabSwitchScenarios++;
}
Console.WriteLine($"PASS: {tabSwitchScenarios} tab activation combinations (default click, optional hover)");

// Display unplug/replug: Explorer may rebuild FolderView and make all native
// icons visible again. Re-hide only when the desktop is active, display is
// stable, settings still require hidden icons and a live visible view exists.
var iconRecoveryCases = 0;
foreach (var desktopStarted in new[] { false, true })
foreach (var safeMode in new[] { false, true })
foreach (var intendedHidden in new[] { false, true })
foreach (var stableDisplay in new[] { false, true })
foreach (bool? listViewVisible in new bool?[] { null, false, true })
{
    var expected = desktopStarted && !safeMode && intendedHidden &&
        stableDisplay && listViewVisible == true;
    var actual = NativeDesktopIconRecoveryPolicy.ShouldRehide(
        desktopStarted, safeMode, intendedHidden, stableDisplay, listViewVisible);
    if (expected != actual)
        throw new Exception($"Native desktop icon repair policy mismatch: runtime={desktopStarted}, safe={safeMode}, hidden={intendedHidden}, stable={stableDisplay}, explorerVisible={listViewVisible}");
    iconRecoveryCases++;
}
Console.WriteLine($"PASS: {iconRecoveryCases} safe native Explorer icon repair combinations");
