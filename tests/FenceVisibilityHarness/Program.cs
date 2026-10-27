using CyanDeck.Appearance;
using CyanDeck.Layout;
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

// The monitor-unplug planner is pure geometry. Every scenario must preserve
// existing groups, metadata and saved layouts without changing desktop files.
if (!MonitorDisconnectLayoutPlanner.IsMonitorReduction(2, 1) ||
    MonitorDisconnectLayoutPlanner.IsMonitorReduction(1, 2) ||
    MonitorDisconnectLayoutPlanner.IsMonitorReduction(2, 2) ||
    MonitorDisconnectLayoutPlanner.IsMonitorReduction(2, 0))
    throw new Exception("Auto rehome triggers only after an actual monitor count decrease.");
var screen = new MonitorDisconnectLayoutPlanner.SurvivingArea("DISPLAY1", 0, 0, 1500, 900, true);
var primaryGroup = new MonitorDisconnectLayoutPlanner.DisconnectedGroup(
    Guid.NewGuid(), 0, false, 40, 45, 500, 400);
var missingGroup = new MonitorDisconnectLayoutPlanner.DisconnectedGroup(
    Guid.NewGuid(), 0, false, 1950, 45, 500, 400);
var globalGroup = new MonitorDisconnectLayoutPlanner.DisconnectedGroup(
    Guid.NewGuid(), 0, true, 650, 55, 330, 290);
var plan = MonitorDisconnectLayoutPlanner.Plan([screen],
    [primaryGroup, missingGroup, globalGroup], 12);
var keep = plan.Placements.Single(p => p.Id == primaryGroup.Id);
var moved = plan.Placements.Single(p => p.Id == missingGroup.Id);
var keepGlobal = plan.Placements.Single(p => p.Id == globalGroup.Id);
if (keep.Relocated || keep.X != 40 || keep.Y != 45 ||
    keepGlobal.Relocated || keepGlobal.X != 650 || keepGlobal.Y != 55 ||
    !moved.Relocated || plan.RelocatedCount != 1 || plan.OverlapUnavoidable ||
    moved.X < 0 || moved.Y < 0 || moved.X + moved.Width > 1500 ||
    moved.Y + moved.Height > 900)
    throw new Exception("Unplug must keep visible group positions and bring only missing groups into bounds.");
static bool Overlap(MonitorDisconnectLayoutPlanner.RehomePlacement a,
    MonitorDisconnectLayoutPlanner.RehomePlacement b) =>
    Math.Min(a.X + a.Width, b.X + b.Width) > Math.Max(a.X, b.X) &&
    Math.Min(a.Y + a.Height, b.Y + b.Height) > Math.Max(a.Y, b.Y);
if (Overlap(moved, keep) || Overlap(moved, keepGlobal))
    throw new Exception("Moved group overlaps an already visible desktop group.");
// Per-page groups may reuse coordinates, but show-on-all-page groups must not.
var otherPage = new MonitorDisconnectLayoutPlanner.DisconnectedGroup(
    Guid.NewGuid(), 2, false, 1940, 50, 500, 400);
var pagePlan = MonitorDisconnectLayoutPlanner.Plan([screen],
    [primaryGroup, globalGroup, otherPage], 12);
if (pagePlan.OverlapUnavoidable ||
    Overlap(pagePlan.Placements.Single(p => p.Id == otherPage.Id),
            pagePlan.Placements.Single(p => p.Id == globalGroup.Id)))
    throw new Exception("All-page groups must reserve their space across desktop pages.");
// A saved one-screen layout should already be within bounds; planner must
// preserve it exactly and never compact a deliberately customized layout.
var savedA = primaryGroup with { X = 95, Y = 100 };
var savedB = missingGroup with { X = 850, Y = 140 };
var savedPlan = MonitorDisconnectLayoutPlanner.Plan([screen], [savedA, savedB]);
if (savedPlan.RelocatedCount != 0 ||
    savedPlan.Placements.Single(p => p.Id == savedA.Id).X != savedA.X ||
    savedPlan.Placements.Single(p => p.Id == savedB.Id).Y != savedB.Y)
    throw new Exception("A stored one-monitor layout must remain intact.");
// Crowded work areas must keep every group on-screen while reporting that
// overlap is unavoidable, rather than writing off-screen coordinates.
var tight = new MonitorDisconnectLayoutPlanner.SurvivingArea("SMALL", 0, 0, 280, 200, true);
var dense = Enumerable.Range(0, 5).Select(i =>
    new MonitorDisconnectLayoutPlanner.DisconnectedGroup(
        Guid.NewGuid(), 0, false, 2000 + i * 100, 2000, 250, 180)).ToArray();
var densePlan = MonitorDisconnectLayoutPlanner.Plan([tight], dense, 12);
if (densePlan.Placements.Count != 5 || !densePlan.OverlapUnavoidable ||
    densePlan.Placements.Any(p => p.X < -1 || p.Y < -1 ||
        p.X + p.Width > 281 || p.Y + p.Height > 201))
    throw new Exception("Crowded monitor must have bounded fallback and a diagnostic overlap flag.");
Console.WriteLine("PASS: monitor unplug automatic placement, saved layout, pages and crowded fallback");

// Partial visual reset: only immutable geometry planning. Retain group IDs,
// page assignments, and user content in the caller, with deterministic layout.
var resetGroups = new[]
{
    new InitialGeometryResetPolicy.ExistingGroup(Guid.NewGuid(), 0, false),
    new InitialGeometryResetPolicy.ExistingGroup(Guid.NewGuid(), 0, false),
    new InitialGeometryResetPolicy.ExistingGroup(Guid.NewGuid(), 0, false),
    new InitialGeometryResetPolicy.ExistingGroup(Guid.NewGuid(), 0, true),
    new InitialGeometryResetPolicy.ExistingGroup(Guid.NewGuid(), 2, false)
};
var resetArea = new MonitorGroupRecoveryPlanner.WorkArea(0, 0, 1920, 1080);
var restored = InitialGeometryResetPolicy.Plan(resetArea, resetGroups, 14);
if (restored.Placements.Count != resetGroups.Length ||
    restored.Placements.Select(p => p.Id).Distinct().Count() != resetGroups.Length ||
    restored.Placements.Any(p => p.X < 0 || p.Y < 0 ||
        p.X + p.Width > 1920 || p.Y + p.Height > 1080) ||
    restored.OverlapUnavoidable)
    throw new Exception("Initial position/size reset must be bounded, complete and collision-free.");
var smallReset = InitialGeometryResetPolicy.Plan(
    new MonitorGroupRecoveryPlanner.WorkArea(0, 0, 950, 620), resetGroups, 10);
if (smallReset.Placements.Count != resetGroups.Length ||
    smallReset.Placements.Any(p => p.Width <= 0 || p.Height <= 0))
    throw new Exception("Small monitor layout reset must keep every group usable.");
// Exact four-group first-install positions, including the compact system
// group below the third group, are restored if the primary screen can fit.
var defaultGroupSet = Enumerable.Range(0, 4).Select(_ =>
    new InitialGeometryResetPolicy.ExistingGroup(Guid.NewGuid(), 0, false)).ToArray();
var initialFour = InitialGeometryResetPolicy.Plan(resetArea, defaultGroupSet, 18).Placements;
if (initialFour[0].X != 36 || initialFour[0].Y != 46 ||
    initialFour[1].X != 419 || initialFour[2].X != 802 ||
    initialFour[3].X != 802 || initialFour[3].Y != 419 ||
    initialFour[3].Height != 260)
    throw new Exception("Default four group placements must match CreateDefaultLayout.");
var defaultsGeometry = InitialGeometryResetPolicy.CalendarDefaults;
if (defaultsGeometry != (120d, 110d, 650d, 510d))
    throw new Exception("Calendar reset must restore only original window geometry.");
Console.WriteLine("PASS: selective geometry reset on desktop groups and calendar defaults");

 
// Tray command routing must be independent of Korean display labels whenever a
// stable Tag ID exists. Every unknown command stays available in Groups.
var trayCases = new (string? Id, string Label, TrayMenuSection Target)[]
{
    ("brand.identity", "CyanDeck · v8.0.24", TrayMenuSection.Tools),
    ("calendar.status", "임의 상태", TrayMenuSection.Calendar),
    ("calendar.enable", "다른 캘린더 표시 문구", TrayMenuSection.Calendar),
    ("calendar.repair", "캘린더 표시 복구", TrayMenuSection.CalendarRepair),
    ("calendar.repair", "캘린더 위치 초기화 / 표시 복구", TrayMenuSection.CalendarRepair),
    ("group.new", "새 창 생성", TrayMenuSection.Primary),
    ("desktop.pages", "페이지 이동", TrayMenuSection.Primary),
    ("desktop.peek", "간편 보기", TrayMenuSection.Primary),
    ("desktop.quick-hide", "화면 숨김", TrayMenuSection.Primary),
    ("desktop.native-icons", "원본 아이콘", TrayMenuSection.IconTools),
    ("desktop.display", "전체 화면", TrayMenuSection.Groups),
    ("group.snapshots", "스냅샷 관리", TrayMenuSection.Groups),
    ("recovery.backup", "복구 저장소", TrayMenuSection.Recovery),
    ("settings.startup", "부팅 시 실행", TrayMenuSection.SettingsOnly),
    (null, "설정…", TrayMenuSection.Primary),
    (null, "새 그룹", TrayMenuSection.Primary),
    (null, "바탕화면 페이지 (페이지 4)", TrayMenuSection.Primary),
    (null, "빠른 숨김 종료", TrayMenuSection.Primary),
    (null, "캘린더 상태: 동기화 대기", TrayMenuSection.Calendar),
    (null, "Google Calendar 동기화", TrayMenuSection.Calendar),
    (null, "이전 세션 복구 데이터 사용 가능…", TrayMenuSection.Recovery),
    (null, "마지막 앱 업데이트 되돌리기…", TrayMenuSection.Recovery),
    (null, "Windows 바탕화면 아이콘 숨기기", TrayMenuSection.IconTools),
    (null, "휴지통 위치 초기화", TrayMenuSection.IconTools),
    (null, "시작 가이드 다시 보기… (일반 모드에서 사용)", TrayMenuSection.Tools),
    (null, "업데이트 확인…", TrayMenuSection.Tools),
    (null, "일반 모드로 다시 시작", TrayMenuSection.Tools),
    (null, "설정 폴더 열기", TrayMenuSection.SettingsOnly),
    (null, "진단 로그 열기", TrayMenuSection.SettingsOnly),
    (null, "진단 묶음 만들기…", TrayMenuSection.SettingsOnly),
    (null, "Windows 시작 시 실행", TrayMenuSection.SettingsOnly),
    (null, "자동 분류 규칙 지금 적용", TrayMenuSection.SettingsOnly),
    (null, "종료", TrayMenuSection.Exit),
    (null, "화면 내용 교환…", TrayMenuSection.Groups),
    (null, "앞으로 새로 추가하는 그룹 메뉴", TrayMenuSection.Groups)
};
foreach (var (id, label, expected) in trayCases)
    if (TrayMenuRoutingPolicy.Route(id, label) != expected)
        throw new Exception($"Tray policy mismatch: {id ?? "(legacy)"} / {label} -> {expected}");
Console.WriteLine($"PASS: {trayCases.Length} tray routes including safe-mode, calendar-only, fallback and settings de-duplication");


// Unified tray recovery must be safe under monitor transitions, have no
// actions in Safe Mode, and work in calendar-only configurations. This is a
// domain test, not a simulated Explorer HWND.
var surfaceCases = new (bool Safe, bool Deck, bool Ready, bool Calendar, bool Stable,
    bool Groups, bool Inspect, bool Blocked)[]
{
    (false, true, true, true, true, true, true, false),
    (false, false, false, true, true, false, true, false), // calendar-only
    (false, true, true, false, true, true, false, false), // deck-only
    (false, false, false, false, true, false, false, false), // no modules
    (true, true, true, true, true, false, false, false), // safe mode
    (false, true, false, true, true, false, true, false), // deck not ready
    (false, true, true, true, false, false, false, true), // display transition
    (false, false, false, true, false, false, false, true), // calendar transition
};
foreach (var x in surfaceCases)
{
    var recoveryPlan = DisplaySurfaceRecoveryPlan.Create(x.Safe,x.Deck,x.Ready,x.Calendar,x.Stable);
    if ((recoveryPlan.RepairGroups,recoveryPlan.InspectCalendar,recoveryPlan.BlockedByDisplayTransition)
        != (x.Groups,x.Inspect,x.Blocked))
        throw new Exception("Unified surface recovery policy mismatch.");
    if (recoveryPlan.HasWork != (x.Groups || x.Inspect))
        throw new Exception("Surface recovery HasWork mismatch.");
}
Console.WriteLine($"PASS: {surfaceCases.Length} visibility recovery scenarios (combined, calendar-only, deck-only, safe-mode, transition)");


// v8.0.26: saved profile restoration must never accept corrupt geometry,
// duplicate IDs or a stale monitor snapshot observed after topology settling.
var monitorIdA = Guid.NewGuid();
var monitorIdB = Guid.NewGuid();
var validSavedGeometry = new[]
{
    new SavedMonitorLayoutSafetyPolicy.Geometry(monitorIdA, -1920, 20, 480, 310, 310),
    new SavedMonitorLayoutSafetyPolicy.Geometry(monitorIdB, 120, 80, 520, 420, 420)
};
if (!SavedMonitorLayoutSafetyPolicy.IsUsable(validSavedGeometry))
    throw new Exception("Valid two-monitor profile wrongly rejected.");
if (SavedMonitorLayoutSafetyPolicy.IsUsable(Array.Empty<SavedMonitorLayoutSafetyPolicy.Geometry>()))
    throw new Exception("Empty saved profile was accepted.");
if (SavedMonitorLayoutSafetyPolicy.IsUsable(new[]
    {validSavedGeometry[0], validSavedGeometry[0]}))
    throw new Exception("Duplicate group ID in saved profile was accepted.");
if (SavedMonitorLayoutSafetyPolicy.IsUsable(new[]
    {new SavedMonitorLayoutSafetyPolicy.Geometry(monitorIdA, double.NaN, 0, 450, 300, 300)}))
    throw new Exception("Non-finite saved position was accepted.");
if (SavedMonitorLayoutSafetyPolicy.IsUsable(new[]
    {new SavedMonitorLayoutSafetyPolicy.Geometry(monitorIdA, 10, 0, -1, 300, 300)}))
    throw new Exception("Negative saved width was accepted.");
if (SavedMonitorLayoutSafetyPolicy.IsUsable(new[]
    {new SavedMonitorLayoutSafetyPolicy.Geometry(monitorIdA, 10, 0, 450, 300, double.PositiveInfinity)}))
    throw new Exception("Invalid expanded height was accepted.");
if (!SavedMonitorLayoutSafetyPolicy.MatchesSettledTopology("dual-screen", 2, "dual-screen", 2))
    throw new Exception("Settled topology was wrongly rejected.");
if (SavedMonitorLayoutSafetyPolicy.MatchesSettledTopology("dual-screen", 2, "single-screen", 1) ||
    SavedMonitorLayoutSafetyPolicy.MatchesSettledTopology("dual-screen", 2, "single-screen", 2) ||
    SavedMonitorLayoutSafetyPolicy.MatchesSettledTopology("dual-screen", 2, "dual-screen", 1) ||
    SavedMonitorLayoutSafetyPolicy.MatchesSettledTopology("", 2, "", 2))
    throw new Exception("Stale or incomplete monitor snapshot was accepted.");
Console.WriteLine("PASS: 9 monitor profile and settled topology safety assertions.");


// Fast update checks must never create a transient HWND. Slow checks show a
// readable status, and a user-requested skip cannot add a presentation delay.
foreach (var checkComplete in new[] { false, true })
foreach (var elapsedMilliseconds in new long[] { 0, 749, 750, 1000, 8000 })
{
    var showChecking = StartupUpdatePresentationPolicy.ShouldShowChecking(
        checkComplete, elapsedMilliseconds);
    var expectedChecking = !checkComplete &&
        elapsedMilliseconds >= StartupUpdatePresentationPolicy.CheckingGraceMilliseconds;
    if (showChecking != expectedChecking)
        throw new Exception("Startup grace-based visibility mismatch.");
}
foreach (var elapsedMilliseconds in new long[] { -5, 0, 200, 949, 950, 2000 })
foreach (var userSkipped in new[] { false, true })
{
    var remainingMs = StartupUpdatePresentationPolicy.RemainingVisibleMilliseconds(
        elapsedMilliseconds, userSkipped);
    var expectedRemaining = userSkipped ? 0 :
        (int)Math.Max(0, 950 - Math.Max(0L, elapsedMilliseconds));
    if (remainingMs != expectedRemaining)
        throw new Exception("Startup anti-flash minimum visibility mismatch.");
}
Console.WriteLine("PASS: 22 startup update status anti-flash and explicit-skip cases");
