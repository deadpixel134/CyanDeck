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
