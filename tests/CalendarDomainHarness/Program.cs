using CyanDeck.Calendar;
using CyanDeck.Services;
using System.Text.Json;
using System.IO.Compression;
using System.Net;
using System.Net.Http;

// First-run setup must be offered only when it cannot overwrite user state.
var setupRoot = Path.Combine(Path.GetTempPath(), "cyandeck-first-run-" + Guid.NewGuid().ToString("N"));
Assert.That(FirstRunOnboardingPolicy.ShouldOffer(setupRoot), "new install gets guided setup");
Directory.CreateDirectory(setupRoot);
try
{
    foreach (var name in new[] { "layout.json", "layout.json.bak", "layout.json.tmp",
        "startup-baseline.json", "calendar-events.json", "calendar-preferences.json" })
    {
        var file = Path.Combine(setupRoot, name);
        File.WriteAllText(file, "existing");
        Assert.That(!FirstRunOnboardingPolicy.ShouldOffer(setupRoot),
            "existing state blocks first-run initialization: " + name);
        File.Delete(file);
    }
    File.WriteAllText(Path.Combine(setupRoot, FirstRunOnboardingPolicy.PendingMarker), "pending");
    File.WriteAllText(Path.Combine(setupRoot, "calendar-preferences.json"), "pending oauth");
    Assert.That(FirstRunOnboardingPolicy.ShouldOffer(setupRoot),
        "interrupted first-run Google login must resume setup");
    File.WriteAllText(Path.Combine(setupRoot, "layout.json.tmp"), "recoverable");
    Assert.That(!FirstRunOnboardingPolicy.ShouldOffer(setupRoot),
        "staged layout is never overwritten by pending onboarding");
    File.Delete(Path.Combine(setupRoot, "layout.json.tmp"));
    File.Delete(Path.Combine(setupRoot, FirstRunOnboardingPolicy.PendingMarker));
    Assert.That(!FirstRunOnboardingPolicy.ShouldOffer(setupRoot),
        "calendar prefs without marker are existing user state");
    File.Delete(Path.Combine(setupRoot, "calendar-preferences.json"));
    Assert.That(!FirstRunOnboardingPolicy.ShouldOffer(setupRoot, safeMode: true) &&
        !FirstRunOnboardingPolicy.ShouldOffer(setupRoot, externalCommand: true) &&
        !FirstRunOnboardingPolicy.ShouldOffer(setupRoot, updateRecoveryPending: true),
        "safe-mode, external invocation, updater recovery block setup");
}
finally { Directory.Delete(setupRoot, recursive: true); }


// ZIP contents are security boundaries even after the manifest signature:
// reject side-by-side roots, duplicate names, traversal, and alternate EXEs.
static bool AcceptUpdateZip(params string[] entryNames)
{
    using var data = new MemoryStream();
    using (var writer = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var name in entryNames)
        {
            using var stream = writer.CreateEntry(name).Open();
            if (!name.EndsWith('/'))
                stream.WriteByte(42);
        }
    }
    data.Position = 0;
    using var reader = new ZipArchive(data, ZipArchiveMode.Read);
    try { UpdatePackageLayoutPolicy.Validate(reader); return true; }
    catch (InvalidDataException) { return false; }
}
Assert.That(AcceptUpdateZip("CyanDeck.exe", "README.md"),
    "legacy package supports flat signed payload");
Assert.That(AcceptUpdateZip("CyanDeck-7.49.6-win-x64/", "CyanDeck-7.49.6-win-x64/CyanDeck.exe", "CyanDeck-7.49.6-win-x64/README.md"),
    "legacy package supports one wrapped signed payload");
Assert.That(!AcceptUpdateZip("a/CyanDeck.exe", "b/README.md"),
    "signed package refuses sibling payload directories");
Assert.That(!AcceptUpdateZip("CyanDeck.exe", "sub/README.md"),
    "signed package refuses mixed flat and nested content");
Assert.That(!AcceptUpdateZip("CyanDeck.exe", "cyandeck.exe"),
    "signed package refuses case-insensitive filename collisions");
Assert.That(!AcceptUpdateZip("CyanDeck.exe", "CyanDeck.exe"),
    "signed package refuses duplicate ZIP entries");
Assert.That(!AcceptUpdateZip("CyanDeck.exe", "CyanDeck.exe"),
    "legacy channel refuses uncoordinated CyanDeck executable handoff");
Assert.That(!AcceptUpdateZip("nested/../CyanDeck.exe"),
    "signed package refuses traversal");
Assert.That(!AcceptUpdateZip("README.md"), "signed package requires legacy executable");

// Exercise the production singleton lifecycle on a test-only mutex name.
var singletonName = @"Local\CyanDeck.SingletonLifecycleTest." + Guid.NewGuid().ToString("N");
using (var primary = CyanDeckSingleInstanceGate.TryAcquire(singletonName))
{
    Assert.That(primary.IsFirst, "first process owns the legacy singleton");
    var secondaryRejected = Task.Run(() =>
    {
        using var secondary = CyanDeckSingleInstanceGate.TryAcquire(singletonName);
        return !secondary.IsFirst;
    }).GetAwaiter().GetResult();
    Assert.That(secondaryRejected, "secondary process cannot steal singleton ownership");
}
using (var restarted = CyanDeckSingleInstanceGate.TryAcquire(singletonName))
{
    Assert.That(restarted.IsFirst, "clean shutdown releases singleton for next application");
}
// Standalone CyanDeck uses only its own single-instance guard.

// Repository rename must not route CyanDeck to old DesktopDeck updates.
Assert.That(GitHubReleaseSource.DefaultChannel == "github-release://deadpixel134/CyanDeck",
    "CyanDeck native default update channel uses renamed repository");
Assert.That(GitHubReleaseSource.RequireNativeCyanDeckVersion("v8.0.0") == "8.0.0",
    "CyanDeck release tag passes");
Assert.That(GitHubReleaseSource.RequireNativeCyanDeckVersion("v8.7.1") == "8.7.1",
    "future CyanDeck release tag passes");
var rejectedOldRelease = false;
try { GitHubReleaseSource.RequireNativeCyanDeckVersion("v7.49.5"); }
catch (InvalidOperationException) { rejectedOldRelease = true; }
Assert.That(rejectedOldRelease, "old DesktopDeck v7 release is never a CyanDeck update");
var rejectedTagInjection = false;
try { GitHubReleaseSource.RequireNativeCyanDeckVersion("v8.0.0-preview"); }
catch (InvalidDataException) { rejectedTagInjection = true; }
Assert.That(rejectedTagInjection, "only fully versioned stable CyanDeck releases qualify");
Assert.That(GitHubReleaseSource.PackageSource(GitHubReleaseSource.DefaultChannel, "CyanDeck-8.0.0-win-x64.zip")
    == "github-release://deadpixel134/CyanDeck/CyanDeck-8.0.0-win-x64.zip",
    "renamed GitHub repo package source stays within expected channel");

Assert.That(CalendarHeaderPolicy.FormatToday(new DateTime(2026, 10, 9)) == "2026.10.09 (금)",
    "Korean calendar header includes zero-padded date and correct weekday");
Assert.That(CalendarHeaderPolicy.FormatToday(new DateTime(2026, 11, 1)) == "2026.11.01 (일)",
    "calendar header correctly formats month changes and Sundays");
Assert.That(CalendarHeaderPolicy.FormatVisibleRangeAndToday(
    new DateTime(2026, 9, 20), new DateTime(2026, 10, 31),
    CalendarHeaderPolicy.FormatToday(new DateTime(2026, 10, 9)))
    == "2026.09.20 ~ 2026.10.31 - 오늘: 2026.10.09 (금)",
    "calendar heading includes the displayed Sunday-Saturday range before today's date");
Assert.That(CalendarSyncIntervalPolicy.Normalize(-1) == CalendarSyncIntervalPolicy.DefaultMinutes
    && CalendarSyncIntervalPolicy.Normalize(1441) == CalendarSyncIntervalPolicy.DefaultMinutes
    && CalendarSyncIntervalPolicy.Normalize(0) == 0
    && CalendarSyncIntervalPolicy.ToInterval(1) == TimeSpan.FromMinutes(1),
    "auto sync intervals validate 0=off, one-minute minimum and bad-data fallback");
Assert.That(CalendarHeaderPolicy.FormatViewportHint(new DateTime(2026, 10, 9), "Month",
    new DateTime(2026, 9, 27), new DateTime(2026, 10, 31)).Contains("2026년 10월 보기"),
    "viewport details remain available when month heading is replaced with today");

// Drag-and-drop date changes retain local wall-clock times, duration and event identity.
var zone = TimeZoneInfo.CreateCustomTimeZone("TestKST", TimeSpan.FromHours(9), "KST", "KST");
var single = new CalendarEvent { Date = new DateTime(2026, 10, 9), EndDate = new DateTime(2026, 10, 9),
    Title = "Shift", CalendarId = "work", RemoteId = "google-123", RemoteETag = "etag-1" };
var shifted = CalendarEventMovePolicy.CreateDraft(single, new DateTime(2026, 10, 12), zone);
Assert.That(shifted.StartDate == new DateTime(2026, 10, 12)
    && shifted.EndDate == new DateTime(2026, 10, 12)
    && single.Date == new DateTime(2026, 10, 9), "all-day event draft moves without mutating original");
var span = new CalendarEvent { Date = new DateTime(2026, 10, 9), EndDate = new DateTime(2026, 10, 11),
    Title = "Trip", CalendarId = "primary" };
var movedSpan = CalendarEventMovePolicy.CreateDraft(span, new DateTime(2026, 11, 2), zone);
Assert.That(movedSpan.EndDate == new DateTime(2026, 11, 4),
    "multi-day all-day event retains its inclusive duration");
var timed = new CalendarEvent { Date = new DateTime(2026, 10, 9), Title = "Night",
    StartAt = new DateTimeOffset(2026, 10, 9, 23, 30, 0, TimeSpan.FromHours(9)),
    EndAt = new DateTimeOffset(2026, 10, 10, 1, 15, 0, TimeSpan.FromHours(9)),
    CalendarId = "work" };
var movedTimed = CalendarEventMovePolicy.CreateDraft(timed, new DateTime(2026, 10, 12), zone);
Assert.That(movedTimed.StartAt?.Hour == 23 && movedTimed.StartAt?.Day == 12
    && movedTimed.EndAt?.Hour == 1 && movedTimed.EndAt?.Day == 13
    && movedTimed.EndAt - movedTimed.StartAt == TimeSpan.FromMinutes(105),
    "overnight timed event keeps local start and end times and duration");
var dragScratch = Path.Combine(Path.GetTempPath(), "cyandeck-date-move-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dragScratch);
try
{
    var originalStore = new CalendarEventStore(Path.Combine(dragScratch, "events.json"));
    originalStore.CommitSnapshot([single]);
    var live = originalStore.Events.Single();
    originalStore.UpsertBatch(CalendarEventMovePolicy.CreateDraft(live, new DateTime(2026, 10, 12), zone), live);
    var saved = new CalendarEventStore(Path.Combine(dragScratch, "events.json")).Events.Single();
    Assert.That(saved.Date == new DateTime(2026, 10, 12)
        && saved.Id == single.Id && saved.RemoteId == single.RemoteId
        && saved.RemoteETag == single.RemoteETag && saved.Dirty && !saved.Deleted,
        "drag move is durable and retains remote metadata for Google sync");
    var blocked = new CalendarEvent { Date = new DateTime(2026, 10, 9), Title = "Reader",
        RemoteReadOnly = true, CalendarId = "shared" };
    originalStore.CommitSnapshot([blocked]);
    var denied = false;
    try { originalStore.UpsertBatch(CalendarEventMovePolicy.CreateDraft(blocked, new DateTime(2026, 10, 11), zone), blocked); }
    catch (InvalidOperationException) { denied = true; }
    Assert.That(denied && originalStore.Events.Single().Date == blocked.Date,
        "read-only event cannot be moved even through store API");
}
finally { Directory.Delete(dragScratch, recursive: true); }

// OAuth application ID is shared, but tokens never are.
var oauthScratch = Path.Combine(Path.GetTempPath(), "cyandeck-oauth-id-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(oauthScratch);
try
{
    var publicId = "123456789012-validdesktopid.apps.googleusercontent.com";
    File.WriteAllText(Path.Combine(oauthScratch, GoogleOAuthClientConfiguration.BundledIdFile), publicId);
    Assert.That(GoogleOAuthClientConfiguration.Resolve(null, oauthScratch) == publicId,
        "distributor's PUBLIC Desktop OAuth client ID is automatically discovered");
    Assert.That(GoogleOAuthClientConfiguration.Resolve("manual-id", oauthScratch) == "manual-id",
        "user-specific override has priority over bundled public ID");
    File.WriteAllText(Path.Combine(oauthScratch, GoogleOAuthClientConfiguration.BundledIdFile), "not-an-id-or-a-token");
    Assert.That(GoogleOAuthClientConfiguration.Resolve(null, oauthScratch) == "",
        "invalid bundled client IDs are never used for login");
    // The client_secret in a Google Desktop OAuth JSON is an installed-app
    // identifier, NOT a Google user refresh/access token.
    var desktopJson = Path.Combine(oauthScratch, GoogleOAuthClientConfiguration.InstalledClientFile);
    File.WriteAllText(desktopJson, JsonSerializer.Serialize(new
    {
        installed = new { client_id = publicId, client_secret = "GOCSPX-test-only-placeholder" }
    }));
    Assert.That(GoogleOAuthClientConfiguration.Resolve(null, oauthScratch) == publicId,
        "desktop OAuth JSON provides the client ID without a manual setting");
    Assert.That(GoogleOAuthClientConfiguration.ResolveSecret(publicId, oauthScratch) == "GOCSPX-test-only-placeholder",
        "matching Desktop OAuth JSON provides the secret for token exchange and refresh");
    var mismatchRejected = false;
    try { GoogleOAuthClientConfiguration.ResolveSecret("another-desktop-id.apps.googleusercontent.com", oauthScratch); }
    catch (InvalidOperationException) { mismatchRejected = true; }
    Assert.That(mismatchRejected, "mismatched manually configured ID cannot use another client's secret");
    File.WriteAllText(desktopJson, "{\"web\":{\"client_id\":\"" + publicId + "\",\"client_secret\":\"test\"}}");
    var webRejected = false;
    try { GoogleOAuthClientConfiguration.ResolveSecret(publicId, oauthScratch); }
    catch (InvalidDataException) { webRejected = true; }
    Assert.That(webRejected, "web OAuth client JSON is refused for native Desktop PKCE");
    File.Delete(desktopJson);
    Assert.That(GoogleOAuthClientConfiguration.ResolveSecret(publicId, oauthScratch) == "",
        "PKCE-only exchange remains available when no Desktop secret file exists");
}
finally { Directory.Delete(oauthScratch, recursive: true); }

Assert.That(new GoogleCalendarSource { AccessRole = "writerWithoutPrivateAccess" }.CanWrite,
    "Google writerWithoutPrivateAccess can create/edit events");
Assert.That(!new GoogleCalendarSource { AccessRole = "reader" }.CanWrite,
    "read-only subscribed calendar cannot be chosen as an upload target");
var multiScratch = Path.Combine(Path.GetTempPath(), "cyandeck-multi-cal-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(multiScratch);
try
{
    var file = Path.Combine(multiScratch, "prefs.json");
    var visible = new CalendarPreferencesStore(file);
    Assert.That(visible.Data.AutoSyncIntervalMinutes == 15,
        "new calendar preferences default to quota-friendly fifteen-minute sync");
    visible.UpdateAutoSyncInterval(17);
    Assert.That(new CalendarPreferencesStore(file).Data.AutoSyncIntervalMinutes == 17,
        "minute-level auto sync preference survives process restart");
    visible.UpdateAutoSyncInterval(0);
    Assert.That(new CalendarPreferencesStore(file).Data.AutoSyncIntervalMinutes == 0,
        "zero disables automatic sync across restarts");
    var badIntervalRejected = false;
    try { visible.UpdateAutoSyncInterval(1441); }
    catch (ArgumentOutOfRangeException) { badIntervalRejected = true; }
    Assert.That(badIntervalRejected && new CalendarPreferencesStore(file).Data.AutoSyncIntervalMinutes == 0,
        "invalid sync interval is rejected without changing persistent data");
    var sources = new[]
    {
        new GoogleCalendarSource { Id = "primary@example.com", Name = "Personal", IsPrimary = true, AccessRole = "owner", SelectedInGoogle = true },
        new GoogleCalendarSource { Id = "work@example.com", Name = "Work", AccessRole = "writer", SelectedInGoogle = true },
        new GoogleCalendarSource { Id = "holiday@example.com", Name = "Holidays", AccessRole = "reader", SelectedInGoogle = false }
    };
    visible.UpdateCatalog(sources);
    Assert.That(!GoogleCalendarCatalogRefreshPolicy.ShouldRefresh(visible.Data, DateTime.UtcNow),
        "just refreshed Google catalog is reused on periodic polling");
    Assert.That(GoogleCalendarCatalogRefreshPolicy.ShouldRefresh(visible.Data,
        DateTime.UtcNow.Add(GoogleCalendarCatalogRefreshPolicy.RefreshPeriod).AddMinutes(1)),
        "Google catalog is refreshed after TTL");
    Assert.That(GoogleCalendarCatalogRefreshPolicy.ShouldRefresh(new CalendarPreferences(), DateTime.UtcNow),
        "older catalog preferences without UTC checkpoint perform initial refresh");
    Assert.That(!GoogleCalendarCatalogRefreshPolicy.ShouldRefresh(new CalendarPreferencesStore(file).Data, DateTime.UtcNow),
        "Google catalog refresh timestamp survives restart");
    Assert.That(visible.Data.SelectedCalendarIds!.SequenceEqual(new[]{ "primary@example.com", "work@example.com" }),
        "initial visible set honors multiple Google-selected calendars");
    visible.SetVisibleCalendars(new[]{ "work@example.com", "holiday@example.com", "work@example.com" });
    Assert.That(visible.Data.SelectedCalendarIds!.Count == 2 && !visible.IsVisible("primary@example.com"),
        "bulk visibility selection removes duplicates and can hide primary");
    visible.UpdateTargetCalendar("work@example.com");
    visible.UpdateCatalog(sources);
    Assert.That(visible.Data.TargetCalendarId == "work@example.com" && visible.Data.SelectedCalendarIds!.Count == 2,
        "refresh preserves independent default upload calendar and visible set");
    visible.SetVisibleCalendars([]);
    visible.UpdateCatalog(sources);
    Assert.That(visible.Data.SelectedCalendarIds!.Count == 0 && !visible.IsVisible("work@example.com"),
        "explicit hide-all selection survives Google catalog refresh");
    Assert.That(new CalendarPreferencesStore(file).Data.TargetCalendarId == "work@example.com",
        "default upload target survives application restart");
    var invalidRejected = false;
    try { visible.SetVisibleCalendars(["wrong-account@example.com"]); }
    catch (ArgumentException) { invalidRejected = true; }
    Assert.That(invalidRejected, "unknown calendar from a different account is rejected");
}
finally { Directory.Delete(multiScratch, recursive: true); }

var rollingSix = CalendarViewportPolicy.Calculate(new DateTime(2026, 10, 9), "Rolling", 6);
Assert.That(rollingSix.WeekCount == 6 && rollingSix.FirstSunday == new DateTime(2026, 9, 20),
    "six-week rolling viewport has two preceding and three following weeks");
var rollingFive = CalendarViewportPolicy.Calculate(new DateTime(2026, 10, 9), "Rolling", 5);
Assert.That(rollingFive.WeekCount == 5 && rollingFive.FirstSunday.AddDays(14) == new DateTime(2026, 10, 4),
    "today remains in middle row for odd row counts");
var february = CalendarViewportPolicy.Calculate(new DateTime(2027, 2, 13), "Month", 3);
Assert.That(february.FirstSunday == new DateTime(2027, 1, 31) && february.WeekCount == 5,
    "monthly view never truncates February even with fewer configured rows");
Assert.That(CalendarViewportPolicy.Navigate(new DateTime(2026, 10, 9), "Rolling", 6, 1)
    == new DateTime(2026, 11, 20), "rolling navigation advances one full viewport");
Assert.That(CalendarViewportPolicy.Navigate(new DateTime(2026, 10, 9), "Month", 6, -1)
    == new DateTime(2026, 9, 9), "monthly navigation changes month");

// Regression: after paging through months, switching back to the today-centered
// rolling mode must produce the same six-week viewport as pressing "오늘".
var currentDay = new DateTime(2026, 10, 20, 14, 41, 0);
var browsedMonth = new DateTime(2026, 12, 9);
var rollingAnchor = CalendarViewportPolicy.AnchorAfterViewModeChange(
    browsedMonth, "Month", "Rolling", currentDay);
Assert.That(rollingAnchor == currentDay.Date,
    "switching from browsed month to rolling mode resets to local today");
var rollingFromModeChange = CalendarViewportPolicy.Calculate(rollingAnchor, "Rolling", 6);
var rollingFromTodayButton = CalendarViewportPolicy.Calculate(currentDay.Date, "Rolling", 6);
Assert.That(rollingFromModeChange == rollingFromTodayButton,
    "rolling-mode switch matches Today button viewport, not previously browsed month");
Assert.That(rollingFromModeChange.FirstSunday.AddDays(14) <= currentDay.Date
    && currentDay.Date < rollingFromModeChange.FirstSunday.AddDays(21),
    "six-week rolling mode places today in the third visible week");
Assert.That(CalendarViewportPolicy.AnchorAfterViewModeChange(
    browsedMonth, "Month", "Month", currentDay) == browsedMonth,
    "changing unrelated settings while browsing month preserves selected month");
Assert.That(CalendarViewportPolicy.AnchorAfterViewModeChange(
    browsedMonth, "Rolling", "Rolling", currentDay) == browsedMonth,
    "reapplying rolling settings does not discard manually paged viewport");
Assert.That(CalendarViewportPolicy.AnchorAfterViewModeChange(
    browsedMonth, "Rolling", "Month", currentDay) == browsedMonth,
    "switching to month view preserves the current date anchor");
Assert.That(CalendarViewportPolicy.AnchorAfterViewModeChange(
    browsedMonth, null, "Rolling", currentDay) == browsedMonth,
    "initial render never unexpectedly resets a saved or caller-provided anchor");
foreach (var rowCount in new[] { 3, 5, 6, 8 })
{
    var viewport = CalendarViewportPolicy.Calculate(rollingAnchor, "Rolling", rowCount);
    var todayWeekIndex = (int)((currentDay.Date - viewport.FirstSunday).TotalDays / 7);
    Assert.That(todayWeekIndex == (rowCount - 1) / 2,
        $"today belongs to centered week for {rowCount}-row rolling viewport");
}



Assert.That(CalendarWeekSizingPolicy.MinHeightPerWeek(650, 24, 6) >
    CalendarWeekSizingPolicy.MinHeightPerWeek(350, 24, 6),
    "taller calendar stretches six week cells vertically");
Assert.That(CalendarWeekSizingPolicy.MinHeightPerWeek(100, 24, 6) == 42,
    "crowded month never collapses cells below minimum");
Assert.That(CalendarWeekSizingPolicy.MinHeightPerWeek(double.NaN, 24, 6) == 42,
    "unmeasured viewport keeps a safe cell minimum");
var monday = new DateTime(2026, 10, 5);
var sunday = monday.AddDays(-1);
var multiday = new CalendarEvent { Date = monday, EndDate = monday.AddDays(2), Title = "three days" };
var oneDay = new CalendarEvent { Date = monday, Title = "one day" };
var overlap = WeekBarLayout.Build(sunday, [multiday, oneDay]);
Assert.That(overlap.Count == 2, "both events rendered");
Assert.That(overlap.Single(x => x.Event == multiday).ColumnSpan == 3, "three-day bar spans three columns");
Assert.That(overlap.Select(x => x.Lane).Distinct().Count() == 2, "overlap allocated to separate lanes");
var crowded = Enumerable.Range(0, 10).Select(i => new CalendarEvent
{ Date = monday, EndDate = monday.AddDays(1), Title = "overlap " + i }).ToArray();
var crowdedBars = WeekBarLayout.Build(sunday, crowded);
Assert.That(crowdedBars.Count == 10 && crowdedBars.Select(x => x.Lane).Distinct().Count() == 10,
    "ten simultaneous events get distinct visible rows");
// Regression: all-day bars on Wednesday/Thursday must not push Tuesday's
// time-specific events down. A three-day and two-day all-day overlap has two
// distinct, continuous lanes only on the affected dates.
var localWeekStart = new DateTime(2026, 10, 11); // Sunday
var tue13 = localWeekStart.AddDays(2);
var wed14 = localWeekStart.AddDays(3);
var thu15 = localWeekStart.AddDays(4);
var fri16 = localWeekStart.AddDays(5);
var sat17 = localWeekStart.AddDays(6);
var weekOffset = TimeZoneInfo.Local.GetUtcOffset(tue13);
CalendarEvent TimedEvent(DateTime date, int hour, string title) => new()
{
    Date = date, Title = title,
    StartAt = new DateTimeOffset(date.Year, date.Month, date.Day, hour, 0, 0, weekOffset),
    EndAt = new DateTimeOffset(date.Year, date.Month, date.Day, hour + 1, 0, 0, weekOffset)
};
var tueMorning = TimedEvent(tue13, 10, "Tuesday 10");
var tueAfternoon = TimedEvent(tue13, 13, "Tuesday 13");
var tueEvening = TimedEvent(tue13, 22, "Tuesday 22");
var wedTimed = TimedEvent(wed14, 11, "Wednesday timed");
var thuTimed = TimedEvent(thu15, 11, "Thursday timed");
var satTimed = TimedEvent(sat17, 11, "Saturday timed");
var threeDayAllDay = new CalendarEvent { Date = wed14, EndDate = fri16, Title = "3-day all-day" };
var twoDayAllDay = new CalendarEvent { Date = thu15, EndDate = fri16, Title = "2-day all-day" };
var independentDates = WeekBarLayout.Build(localWeekStart,
    [tueEvening, twoDayAllDay, satTimed, thuTimed, tueMorning, threeDayAllDay, wedTimed, tueAfternoon]);
int LaneOf(IReadOnlyList<WeekBar> bars, CalendarEvent calendarEvent) =>
    bars.Single(x => ReferenceEquals(x.Event, calendarEvent)).Lane;
Assert.That(LaneOf(independentDates, tueMorning) == 0 &&
    LaneOf(independentDates, tueAfternoon) == 1 &&
    LaneOf(independentDates, tueEvening) == 2,
    "Tuesday timed items begin at the top despite later all-day spans and preserve time order");
Assert.That(LaneOf(independentDates, threeDayAllDay) == 0 &&
    LaneOf(independentDates, twoDayAllDay) == 1 &&
    independentDates.Single(x => ReferenceEquals(x.Event, threeDayAllDay)).ColumnSpan == 3 &&
    independentDates.Single(x => ReferenceEquals(x.Event, twoDayAllDay)).ColumnSpan == 2,
    "overlapping three-day and two-day all-day bars remain continuous in separate lanes");
Assert.That(LaneOf(independentDates, wedTimed) == 1 &&
    LaneOf(independentDates, thuTimed) == 2 &&
    LaneOf(independentDates, satTimed) == 0,
    "a date's timed entries begin below only the all-day lanes on that date");
// Staggered ranges: B's lane stays fixed even after A ends. An empty lower
// lane on Saturday must not cause the B bar to jump or timed items to overlap.
var staggerA = new CalendarEvent { Date = wed14, EndDate = fri16, Title = "A: Wed-Fri" };
var staggerB = new CalendarEvent { Date = fri16, EndDate = sat17, Title = "B: Fri-Sat" };
var staggered = WeekBarLayout.Build(localWeekStart, [satTimed, staggerB, staggerA]);
Assert.That(LaneOf(staggered, staggerA) == 0 &&
    LaneOf(staggered, staggerB) == 1 &&
    LaneOf(staggered, satTimed) == 2,
    "staggered all-day events retain their multi-day lanes; Saturday timed event clears remaining bar");
// A timed bar spanning Thursday and Friday must clear every all-day lane on
// both dates, including a two-day event beginning after the timed start date.
var thuToFriTimed = new CalendarEvent
{
    Date = thu15, Title = "Timed Thursday-Friday",
    StartAt = new DateTimeOffset(thu15.Year, thu15.Month, thu15.Day, 23, 0, 0, weekOffset),
    EndAt = new DateTimeOffset(sat17.Year, sat17.Month, sat17.Day, 0, 0, 0, weekOffset)
};
var spanWithTimed = WeekBarLayout.Build(localWeekStart, [threeDayAllDay, twoDayAllDay, thuToFriTimed]);
Assert.That(LaneOf(spanWithTimed, thuToFriTimed) == 2 &&
    spanWithTimed.Single(x => ReferenceEquals(x.Event, thuToFriTimed)).ColumnSpan == 2,
    "a timed multi-day bar stays below all-day occupancy throughout its full column span");
// No per-day cross-talk after crossing into a different calendar week.
var nextSundayTimed = TimedEvent(localWeekStart.AddDays(7), 9, "Next Sunday");
var nextWeek = WeekBarLayout.Build(localWeekStart.AddDays(7), [threeDayAllDay, twoDayAllDay, nextSundayTimed]);
Assert.That(nextWeek.Count == 1 && LaneOf(nextWeek, nextSundayTimed) == 0,
    "previous week's all-day occupancy does not affect the next week's dated items");

// All-day bars take the top lanes; timed bars follow increasing local start time.
// Unsorted input must not make the visible order depend on Google sync arrival.
var localOffsetForSorting = TimeZoneInfo.Local.GetUtcOffset(monday);
var early = new CalendarEvent { Date = monday, Title = "08:00",
    StartAt = new DateTimeOffset(2026, 10, 5, 8, 0, 0, localOffsetForSorting),
    EndAt = new DateTimeOffset(2026, 10, 5, 9, 0, 0, localOffsetForSorting) };
var late = new CalendarEvent { Date = monday, Title = "15:00",
    StartAt = new DateTimeOffset(2026, 10, 5, 15, 0, 0, localOffsetForSorting),
    EndAt = new DateTimeOffset(2026, 10, 5, 16, 0, 0, localOffsetForSorting) };
var allDay = new CalendarEvent { Date = monday, Title = "All day" };
var sorted = WeekBarLayout.Build(sunday, [late, early, allDay]);
Assert.That(sorted.Single(x => ReferenceEquals(x.Event, allDay)).Lane == 0
    && sorted.Single(x => ReferenceEquals(x.Event, early)).Lane == 1
    && sorted.Single(x => ReferenceEquals(x.Event, late)).Lane == 2,
    "all-day occupies first lane; timed event lanes ascend by start time");
var crossingTimed = new CalendarEvent { Date = sunday, Title = "overnight",
    StartAt = new DateTimeOffset(2026, 10, 4, 23, 0, 0, localOffsetForSorting),
    EndAt = new DateTimeOffset(2026, 10, 6, 1, 0, 0, localOffsetForSorting) };
var recurringAllDay = new CalendarEvent { Date = monday.AddDays(1), Title = "All day Tuesday" };
var crossingOrder = WeekBarLayout.Build(sunday, [crossingTimed, recurringAllDay]);
Assert.That(crossingOrder.Single(x => ReferenceEquals(x.Event, recurringAllDay)).Lane
    < crossingOrder.Single(x => ReferenceEquals(x.Event, crossingTimed)).Lane,
    "all-day bars precede timed bars even when earlier timed events span into that day");
var weekSpanner = new CalendarEvent { Date = sunday.AddDays(5), EndDate = sunday.AddDays(9), Title = "crossing week" };
var first = WeekBarLayout.Build(sunday, [weekSpanner]);
var second = WeekBarLayout.Build(sunday.AddDays(7), [weekSpanner]);
Assert.That(first.Count == 1 && first[0].StartColumn == 5 && first[0].ColumnSpan == 2, "first week clip");
Assert.That(second.Count == 1 && second[0].StartColumn == 0 && second[0].ColumnSpan == 3, "second week continuation");
Assert.That(oneDay.EffectiveEndDate == oneDay.Date, "legacy single-day compatibility");
var localOffset = TimeZoneInfo.Local.GetUtcOffset(monday);
var exclusiveMidnightTimed = new CalendarEvent
{
    Date = monday,
    StartAt = new DateTimeOffset(2026, 10, 5, 22, 0, 0, localOffset),
    EndAt = new DateTimeOffset(2026, 10, 6, 0, 0, 0, localOffset)
};
Assert.That(exclusiveMidnightTimed.EffectiveEndDate == monday, "exclusive midnight end does not add an extra day");
var titles = CalendarEventStore.ParseLines("first\r\n\r\n second \nthird\n ");
Assert.That(titles.SequenceEqual(["first", "second", "third"]), "each nonempty line becomes a separate title");
// Prove that the persisted checkpoint and in-memory edit have the same
// commit boundary. A .tmp DIRECTORY deterministically simulates an I/O error.
var scratch = Path.Combine(Path.GetTempPath(), "cyandeck-calendar-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var path = Path.Combine(scratch, "events.json");
    var store = new CalendarEventStore(path);
    var draft = new CalendarEventDraft { StartDate = monday, EndDate = monday.AddDays(2), CalendarId = "primary", Text = "alpha\nbeta" };
    Assert.That(store.UpsertBatch(draft) == 2, "multiline batch commits two events");
    Assert.That(new CalendarEventStore(path).Events.Count == 2, "committed batch survives restart");
    var before = store.Events[0];
    Directory.CreateDirectory(path + ".tmp");
    Assert.StorageFailure(() => store.UpsertBatch(new CalendarEventDraft
    {
        StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "changed\nnew"
    }, before), "failed update throws on checkpoint error");
    Assert.That(store.Events.Count == 2 && ReferenceEquals(store.Events[0], before) && before.Title == "alpha",
        "failed batch leaves in-memory objects unchanged");
    Assert.StorageFailure(() => store.Delete(before), "failed deletion throws on checkpoint error");
    Assert.That(store.Events.Count == 2 && !before.Deleted, "failed deletion does not mutate memory");
    Directory.Delete(path + ".tmp");
    Assert.That(store.UpsertBatch(new CalendarEventDraft
    {
        StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "revised"
    }, before) == 1, "staged update succeeds once storage recovers");
    Assert.That(store.Events[0].Title == "revised" && before.Title == "alpha",
        "published snapshot replaces the edited object");
    store.Delete(store.Events[0]);
    Assert.That(new CalendarEventStore(path).Events.Count == 1,
        "successful deletion persists across restart");
    // Imported pages are staged and published only after a successful write.
    var liveBefore = store.Events[0];
    var failedImport = store.CreateSnapshot();
    failedImport[0].Title = "remote revision";
    failedImport.Add(new CalendarEvent { Date = monday, Title = "new remote event", Dirty = false });
    Directory.CreateDirectory(path + ".tmp");
    Assert.StorageFailure(() => store.CommitSnapshot(failedImport),
        "failed remote-page checkpoint throws");
    Assert.That(ReferenceEquals(store.Events[0], liveBefore) && store.Events.Count == 1
        && store.Events[0].Title != "remote revision",
        "failed remote-page checkpoint keeps live collection unchanged");
    Assert.That(new CalendarEventStore(path).Events.Count == 1,
        "failed remote-page checkpoint keeps persisted collection unchanged");
    Directory.Delete(path + ".tmp");
    store.CommitSnapshot(failedImport);
    Assert.That(store.Events.Count == 2 && store.Events[0].Title == "remote revision",
        "successful remote-page checkpoint publishes staged collection");
    Assert.That(new CalendarEventStore(path).Events.Count == 2,
        "successful remote-page checkpoint survives restart");

    var prefsPath = Path.Combine(scratch, "preferences.json");
    var prefs = new CalendarPreferencesStore(prefsPath);
    prefs.UpdateCatalog([new GoogleCalendarSource
    {
        Id = "personal@example.com", Name = "Personal", AccessRole = "owner", IsPrimary = true
    }]);
    prefs.UpdateSyncToken("personal@example.com", "token-old");
    Assert.That(prefs.Data.SyncTokens["personal@example.com"] == "token-old",
        "initial sync token checkpoint committed");
    var oldData = prefs.Data;
    Directory.CreateDirectory(prefsPath + ".tmp");
    Assert.StorageFailure(() => prefs.UpdateSyncToken("personal@example.com", "token-new"),
        "failed sync-token checkpoint throws");
    Assert.That(ReferenceEquals(prefs.Data, oldData)
        && prefs.Data.SyncTokens["personal@example.com"] == "token-old",
        "failed sync-token checkpoint leaves memory unchanged");
    Assert.StorageFailure(() => prefs.UpdateCatalog([new GoogleCalendarSource
    {
        Id = "wrong@example.com", Name = "Wrong", AccessRole = "owner"
    }]), "failed catalog checkpoint throws");
    Assert.That(ReferenceEquals(prefs.Data, oldData)
        && prefs.Data.Catalog.Single().Id == "personal@example.com",
        "failed catalog checkpoint leaves memory unchanged");
    Assert.That(new CalendarPreferencesStore(prefsPath).Data.SyncTokens["personal@example.com"] == "token-old",
        "failed preferences checkpoints leave persisted sync token unchanged");
    Directory.Delete(prefsPath + ".tmp");
    prefs.UpdateSyncToken("personal@example.com", "token-new");
    Assert.That(new CalendarPreferencesStore(prefsPath).Data.SyncTokens["personal@example.com"] == "token-new",
        "successful sync-token checkpoint survives restart");
    prefs.UpdateSyncToken("personal@example.com", null);
    Assert.That(!prefs.Data.SyncTokens.ContainsKey("personal@example.com"),
        "clearing expired sync token is persistent");
    // UI preference edits must be as atomic as remote sync-token edits.
    var stablePreferences = prefs.Data;
    Directory.CreateDirectory(prefsPath + ".tmp");
    Assert.StorageFailure(() => prefs.UpdateTargetCalendar("other@example.com"),
        "failed target-calendar write throws");
    Assert.StorageFailure(() => prefs.SetCalendarVisibility("personal@example.com", false),
        "failed visible-calendar write throws");
    Assert.StorageFailure(() => prefs.SetColorOverride("personal@example.com", "#aabbcc"),
        "failed color override write throws");
    Assert.That(ReferenceEquals(prefs.Data, stablePreferences)
        && prefs.Data.TargetCalendarId == "personal@example.com"
        && prefs.Data.SelectedCalendarIds!.Contains("personal@example.com")
        && !prefs.Data.ColorOverrides.ContainsKey("personal@example.com"),
        "failed UI settings leave live preferences entirely unchanged");
    var durableBefore = new CalendarPreferencesStore(prefsPath);
    Assert.That(durableBefore.Data.TargetCalendarId == "personal@example.com"
        && durableBefore.Data.SelectedCalendarIds!.Contains("personal@example.com")
        && durableBefore.Data.ColorOverrides.Count == 0,
        "failed UI settings leave persisted preferences entirely unchanged");
    Directory.Delete(prefsPath + ".tmp");
    prefs.UpdateTargetCalendar("personal@example.com");
    prefs.SetCalendarVisibility("personal@example.com", false);
    prefs.SetColorOverride("personal@example.com", "#aabbcc");
    var durableAfter = new CalendarPreferencesStore(prefsPath);
    Assert.That(durableAfter.Data.TargetCalendarId == "personal@example.com"
        && !durableAfter.Data.SelectedCalendarIds!.Contains("personal@example.com")
        && durableAfter.Data.ColorOverrides["personal@example.com"] == "#AABBCC",
        "successful UI preference update survives restart and normalizes color");
    prefs.SetCalendarVisibility("personal@example.com", true);
    prefs.SetCalendarVisibility("personal@example.com", true);
    Assert.That(prefs.Data.SelectedCalendarIds!.Count(id => id == "personal@example.com") == 1,
        "reselecting an already enabled calendar does not add duplicates");
    prefs.SetColorOverride("personal@example.com", null);
    Assert.That(!prefs.Data.ColorOverrides.ContainsKey("personal@example.com"),
        "clearing an override restores the remote color");
    try { prefs.SetColorOverride("personal@example.com", "red"); throw new Exception("FAIL: invalid override accepted"); }
    catch (ArgumentException) { Console.WriteLine("PASS: invalid override rejected before writing"); }

    // The .bak file contains the checkpoint preceding the last successful
    // File.Replace. Corrupting the primary must expose that older data for
    // reading WITHOUT ever authorizing a new write or Google synchronization.
    var recoveringEventsPath = Path.Combine(scratch, "recover-events.json");
    var recoveryStore = new CalendarEventStore(recoveringEventsPath);
    recoveryStore.UpsertBatch(new CalendarEventDraft
    {
        StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "safe backup"
    });
    recoveryStore.UpsertBatch(new CalendarEventDraft
    {
        StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "later primary"
    });
    var damagedEvents = "{invalid calendar events";
    File.WriteAllText(recoveringEventsPath, damagedEvents);
    var restoredEvents = new CalendarEventStore(recoveringEventsPath);
    Assert.That(restoredEvents.IsRecoveredFromBackup && restoredEvents.IsReadOnly
        && restoredEvents.Events.Count == 1 && restoredEvents.Events[0].Title == "safe backup",
        "corrupt events primary displays older backup read-only");
    Assert.StorageFailure(() => restoredEvents.UpsertBatch(new CalendarEventDraft
    {
        StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "blocked"
    }), "events backup recovery forbids new writes");
    Assert.That(File.ReadAllText(recoveringEventsPath) == damagedEvents,
        "recovered events never overwrite corrupt primary");
    File.WriteAllText(recoveringEventsPath, "null");
    Assert.That(new CalendarEventStore(recoveringEventsPath).IsRecoveredFromBackup,
        "null JSON cannot silently reset event data");
    File.Delete(recoveringEventsPath);
    var missingEvents = new CalendarEventStore(recoveringEventsPath);
    Assert.That(missingEvents.IsRecoveredFromBackup && missingEvents.IsReadOnly,
        "missing primary with existing event backup is not treated as new install");

    var recoveringPreferencesPath = Path.Combine(scratch, "recover-preferences.json");
    var recoveryPrefs = new CalendarPreferencesStore(recoveringPreferencesPath);
    recoveryPrefs.UpdateCatalog([new GoogleCalendarSource
    {
        Id = "safe@example.com", Name = "Safe", AccessRole = "writer", IsPrimary = true
    }]);
    recoveryPrefs.SetColorOverride("safe@example.com", "#112233");
    File.WriteAllText(recoveringPreferencesPath, "{broken preferences");
    var restoredPrefs = new CalendarPreferencesStore(recoveringPreferencesPath);
    Assert.That(restoredPrefs.IsReadOnly && restoredPrefs.IsRecoveredFromBackup
        && restoredPrefs.Data.Catalog.Single().Id == "safe@example.com"
        && restoredPrefs.Data.ColorOverrides.Count == 0,
        "corrupt preferences primary displays older catalog safely");
    Assert.StorageFailure(() => restoredPrefs.UpdateSyncToken("safe@example.com", "blocked"),
        "recovered preferences cannot advance sync tokens");
    Assert.That(File.ReadAllText(recoveringPreferencesPath) == "{broken preferences",
        "preferences backup recovery never overwrites corrupt primary");
    File.WriteAllText(recoveringPreferencesPath, "null");
    Assert.That(new CalendarPreferencesStore(recoveringPreferencesPath).IsRecoveredFromBackup,
        "null preferences JSON cannot silently create default settings");
    File.Delete(recoveringPreferencesPath);
    Assert.That(new CalendarPreferencesStore(recoveringPreferencesPath).IsReadOnly,
        "missing preferences primary with existing backup blocks writes");

    var unrecoverablePath = Path.Combine(scratch, "unrecoverable-events.json");
    File.WriteAllText(unrecoverablePath, "{broken");
    var unrecoverable = new CalendarEventStore(unrecoverablePath);
    Assert.That(unrecoverable.IsReadOnly && !unrecoverable.IsRecoveredFromBackup,
        "corrupt events without backup remain safely read-only");
}
finally { Directory.Delete(scratch, recursive: true); }
// v15: explicit, confirmed recovery preserves .bak and archives damaged primary.
var restoreScratch = Path.Combine(Path.GetTempPath(), "cyandeck-manual-restore-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(restoreScratch);
try
{
    var eventsPath = Path.Combine(restoreScratch, "events.json");
    var initial = new CalendarEventStore(eventsPath);
    initial.UpsertBatch(new CalendarEventDraft
    { StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "safe snapshot" });
    initial.UpsertBatch(new CalendarEventDraft
    { StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "newer snapshot" });
    var backupBytes = File.ReadAllBytes(eventsPath + ".bak");
    File.WriteAllText(eventsPath, "{invalid original");
    var readOnlyEvents = new CalendarEventStore(eventsPath);
    Assert.That(readOnlyEvents.IsReadOnly && readOnlyEvents.IsRecoveredFromBackup,
        "v15 event recovery starts only from a validated read-only backup");
    var archived = readOnlyEvents.RestoreFromBackup();
    Assert.That(!readOnlyEvents.IsReadOnly && !readOnlyEvents.IsRecoveredFromBackup
        && readOnlyEvents.Events.Single().Title == "safe snapshot",
        "v15 event restore explicitly re-enables edits after durable replacement");
    Assert.That(archived is not null && File.ReadAllText(archived) == "{invalid original",
        "v15 damaged event primary remains quarantined");
    Assert.That(File.ReadAllBytes(eventsPath + ".bak").SequenceEqual(backupBytes),
        "v15 original known-good event backup remains untouched");
    Assert.That(new CalendarEventStore(eventsPath).Events.Single().Title == "safe snapshot",
        "v15 restored event content survives restart");

    var prefsPath = Path.Combine(restoreScratch, "preferences.json");
    var prefs = new CalendarPreferencesStore(prefsPath);
    prefs.UpdateCatalog([new GoogleCalendarSource { Id = "account@example.com", Name = "Account", AccessRole = "owner", IsPrimary = true }]);
    prefs.UpdateSyncToken("account@example.com", "old-token");
    prefs.UpdateSyncToken("account@example.com", "newer-token");
    // The backup now deliberately contains a STALE, non-empty sync token.
    File.WriteAllText(prefsPath, "{broken settings");
    var readOnlyPrefs = new CalendarPreferencesStore(prefsPath);
    Assert.That(readOnlyPrefs.IsRecoveredFromBackup, "v15 preferences backup is available");
    var archivedPreferences = readOnlyPrefs.RestoreFromBackup();
    Assert.That(archivedPreferences is not null && File.ReadAllText(archivedPreferences) == "{broken settings",
        "v15 damaged preferences primary remains quarantined");
    Assert.That(readOnlyPrefs.Data.SyncTokens["account@example.com"] == "old-token",
        "v15 restored preference snapshot contains stale token requiring invalidation");
    readOnlyPrefs.InvalidateAllSyncTokens();
    Assert.That(!readOnlyPrefs.IsReadOnly && readOnlyPrefs.Data.SyncTokens.Count == 0
        && new CalendarPreferencesStore(prefsPath).Data.SyncTokens.Count == 0,
        "v15 recovery clears old per-calendar sync tokens durably");

    var missingPath = Path.Combine(restoreScratch, "missing.json");
    var missing = new CalendarEventStore(missingPath);
    missing.UpsertBatch(new CalendarEventDraft
    { StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "rebuild missing" });
    missing.UpsertBatch(new CalendarEventDraft
    { StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "newer" });
    File.Delete(missingPath);
    var missingStore = new CalendarEventStore(missingPath);
    Assert.That(missingStore.RestoreFromBackup() is null
        && new CalendarEventStore(missingPath).Events.Single().Title == "rebuild missing",
        "v15 missing primary is restored without inventing a damaged-primary archive");

    var invalidatedPath = Path.Combine(restoreScratch, "invalidated.json");
    var invalidated = new CalendarEventStore(invalidatedPath);
    invalidated.UpsertBatch(new CalendarEventDraft
    { StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "old" });
    invalidated.UpsertBatch(new CalendarEventDraft
    { StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "recent" });
    File.WriteAllText(invalidatedPath, "{bad primary");
    var invalidatedStore = new CalendarEventStore(invalidatedPath);
    File.WriteAllText(invalidatedPath + ".bak", "null");
    // A valid read-only backup may be changed externally before confirmation.
    // Its now-invalid JSON must be rejected with InvalidDataException, not
    // mistaken for an IOException/permission failure by the test harness.
    Assert.InvalidBackupFailure(() => invalidatedStore.RestoreFromBackup(),
        "Calendar events JSON contains null rather than an event array.",
        "v15 invalidated backup fails re-validation before modifying primary (InvalidDataException)");
    Assert.That(invalidatedStore.IsReadOnly && invalidatedStore.IsRecoveredFromBackup
        && File.ReadAllText(invalidatedPath) == "{bad primary"
        && File.ReadAllText(invalidatedPath + ".bak") == "null"
        && Directory.GetFiles(restoreScratch, "invalidated.json.restore-*.tmp").Length == 0,
        "v15 failed event restore preserves primary and backup, cleans staging, and remains read-only");

    var invalidPrefsPath = Path.Combine(restoreScratch, "invalidated-preferences.json");
    var invalidPrefs = new CalendarPreferencesStore(invalidPrefsPath);
    invalidPrefs.UpdateCatalog([new GoogleCalendarSource
    { Id = "account@example.com", Name = "Account", AccessRole = "owner", IsPrimary = true }]);
    invalidPrefs.UpdateSyncToken("account@example.com", "old-token");
    invalidPrefs.UpdateSyncToken("account@example.com", "new-token");
    File.WriteAllText(invalidPrefsPath, "{bad preferences primary");
    var invalidPrefsStore = new CalendarPreferencesStore(invalidPrefsPath);
    Assert.That(invalidPrefsStore.IsReadOnly && invalidPrefsStore.IsRecoveredFromBackup,
        "v15 preferences backup initially validates before external modification");
    File.WriteAllText(invalidPrefsPath + ".bak", "null");
    Assert.InvalidBackupFailure(() => invalidPrefsStore.RestoreFromBackup(),
        "Calendar preferences JSON is null.",
        "v15 invalidated preferences backup fails staged JSON validation");
    Assert.That(invalidPrefsStore.IsReadOnly && invalidPrefsStore.IsRecoveredFromBackup
        && File.ReadAllText(invalidPrefsPath) == "{bad preferences primary"
        && File.ReadAllText(invalidPrefsPath + ".bak") == "null"
        && Directory.GetFiles(restoreScratch, "invalidated-preferences.json.restore-*.tmp").Length == 0,
        "v15 failed preference restore preserves primary and backup, cleans staging, and remains read-only");
}
finally { Directory.Delete(restoreScratch, recursive: true); }

// Upload metadata must be atomic even when the remote request has succeeded.
var uploadScratch = Path.Combine(Path.GetTempPath(), "cyandeck-upload-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(uploadScratch);
try
{
    var path = Path.Combine(uploadScratch, "uploaded-events.json");
    var store = new CalendarEventStore(path);
    store.UpsertBatch(new CalendarEventDraft
    {
        StartDate = monday, EndDate = monday, CalendarId = "primary", Text = "pending"
    });
    var uploaded = store.Events.Single();
    Directory.CreateDirectory(path + ".tmp");
    Assert.StorageFailure(() => store.CommitEventChange(uploaded.Id, e =>
    {
        e.RemoteId = "dd" + e.Id.ToString("N");
        e.RemoteETag = "etag-ack";
        e.RemoteCreatePending = false;
        e.Dirty = false;
        return true;
    }), "failed upload ACK checkpoint throws");
    Assert.That(ReferenceEquals(uploaded, store.Events.Single())
        && uploaded.RemoteId is null && uploaded.Dirty,
        "failed upload ACK leaves live object and pending edit unchanged");
    Assert.That(new CalendarEventStore(path).Events.Single().Dirty,
        "failed upload ACK leaves persisted pending edit unchanged");
    Assert.StorageFailure(() => store.CommitEventRemoval(uploaded.Id),
        "failed remote DELETE checkpoint throws");
    Assert.That(store.Events.Count == 1 && new CalendarEventStore(path).Events.Count == 1,
        "failed DELETE checkpoint cannot discard event in memory or on disk");
    Directory.Delete(path + ".tmp");
    Assert.That(store.CommitEventChange(uploaded.Id, e =>
    {
        e.RemoteId = "dd" + e.Id.ToString("N");
        e.RemoteETag = "etag-ack";
        e.RemoteCreatePending = false;
        e.Dirty = false;
        return true;
    }), "upload ACK checkpoint succeeds once filesystem is writable");
    Assert.That(!new CalendarEventStore(path).Events.Single().Dirty,
        "upload ACK checkpoint survives restart");
    Assert.That(store.CommitEventRemoval(uploaded.Id)
        && new CalendarEventStore(path).Events.Count == 0,
        "acknowledged DELETE is persisted as a removal");
}
finally { Directory.Delete(uploadScratch, recursive: true); }

// Upload ACK content is compared against both the sent and current revisions.
using (var remoteAck = JsonDocument.Parse("""
    {"id":"dd12345","etag":"etag-2","summary":"before",
     "start":{"date":"2026-10-05"},"end":{"date":"2026-10-06"}}
    """))
{
    var sent = new CalendarEvent { Date = monday, Title = "before", CalendarId = "primary" };
    var current = new CalendarEvent { Date = monday, Title = "before", CalendarId = "primary" };
    Assert.That(GoogleUploadReconciliation.Resolve(remoteAck.RootElement, sent, current)
        == GoogleUploadReconciliation.Resolution.Clean,
        "acknowledged upload matches current data");
    current.Title = "edited while uploading";
    Assert.That(GoogleUploadReconciliation.Resolve(remoteAck.RootElement, sent, current)
        == GoogleUploadReconciliation.Resolution.LocalEditPending,
        "later local edits remain pending after remote ACK");
    current.Title = "before";
    current.Deleted = true;
    Assert.That(GoogleUploadReconciliation.Resolve(remoteAck.RootElement, sent, current)
        == GoogleUploadReconciliation.Resolution.LocalEditPending,
        "local deletion during create/patch ACK cannot be silently forgotten");
    current.Deleted = false;
    using var remoteChanged = JsonDocument.Parse("""
        {"id":"dd12345","etag":"etag-3","summary":"different on server",
         "start":{"date":"2026-10-05"},"end":{"date":"2026-10-06"}}
        """);
    Assert.That(GoogleUploadReconciliation.Resolve(remoteChanged.RootElement, sent, current)
        == GoogleUploadReconciliation.Resolution.Conflict,
        "unrelated server data cannot silently acknowledge a PATCH");
}
// A remote event with the same client-generated ID may contain older payload
// if the successful POST acknowledgement was lost and the user edited locally.
using (var remote = JsonDocument.Parse("""
    {"summary":"alpha","start":{"date":"2026-10-05"},"end":{"date":"2026-10-08"}}
    """))
{
    var snapshot = new CalendarEvent { Date = monday, EndDate = monday.AddDays(2), Title = "alpha" };
    Assert.That(GoogleEventReconciliation.MatchesLocalContent(remote.RootElement, snapshot),
        "409 recovery: identical all-day content is safe to acknowledge");
    snapshot.Title = "changed after POST";
    Assert.That(!GoogleEventReconciliation.MatchesLocalContent(remote.RootElement, snapshot),
        "409 recovery: later local title edit must remain dirty/conflicted");
    snapshot.Title = "alpha";
    snapshot.EndDate = monday.AddDays(1);
    Assert.That(!GoogleEventReconciliation.MatchesLocalContent(remote.RootElement, snapshot),
        "409 recovery: later local date edit must not be silently discarded");
}
using (var remote = JsonDocument.Parse("""
    {"summary":"call","start":{"dateTime":"2026-10-05T09:00:00+09:00"},
    "end":{"dateTime":"2026-10-05T10:00:00+09:00"}}
    """))
{
    var timedEvent = new CalendarEvent
    {
        Title = "call", StartAt = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero),
        EndAt = new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero)
    };
    Assert.That(GoogleEventReconciliation.MatchesLocalContent(remote.RootElement, timedEvent),
        "409 recovery: timed UTC instants match across offsets");
    timedEvent.EndAt = timedEvent.EndAt.Value.AddMinutes(15);
    Assert.That(!GoogleEventReconciliation.MatchesLocalContent(remote.RootElement, timedEvent),
        "409 recovery: timed end edit must remain pending");
}
using (var remote = JsonDocument.Parse("""
    {"summary":"wrong","start":{"date":"2026-10-05"},"end":{"date":"2026-10-06"}}
    """))
{
    var local = new CalendarEvent { Title = "right", Date = monday, EndDate = monday };
    Assert.That(!GoogleEventReconciliation.MatchesLocalContent(remote.RootElement, local),
        "409 recovery: mismatched summary cannot be accepted");
}
var sameEtagLocal = new CalendarEvent
{
    CalendarId = "user@example.com", RemoteETag = "same-etag", Dirty = false,
    RemoteReadOnly = false, Title = "outdated local edit"
};
Assert.That(!GoogleEventReconciliation.ShouldApplyRemote(false, sameEtagLocal, "same-etag", "user@example.com", false),
    "ordinary incremental sync can skip equal ETag cached event");
Assert.That(GoogleEventReconciliation.ShouldApplyRemote(true, sameEtagLocal, "same-etag", "user@example.com", false),
    "forced full sync must refresh clean cached event even with equal ETag");

var colorSettings = new CalendarPreferences
{
    Catalog = [new GoogleCalendarSource { Id = "user@example.com", Name = "Main", IsPrimary = true, Color = "#AABBCC" }],
    ColorOverrides = new Dictionary<string, string> { ["user@example.com"] = "#DDEEFF" }
};
Assert.That(CalendarPreferencesStore.ColorFor(colorSettings, "primary") == "#DDEEFF",
    "primary alias applies actual Google calendar color override");
Assert.That(CalendarPreferencesStore.ColorFor(colorSettings, "user@example.com") == "#DDEEFF",
    "primary actual ID applies the same override");
Assert.That(CalendarPreferencesStore.ColorFor(colorSettings, null) == "#DDEEFF",
    "legacy null calendar uses primary override");
// v8: A new OAuth credential must be verified BEFORE replacing the durable one.
// The checkpoint uses a fake persistence callback: no Google login or DPAPI needed.
var credential = new VerifiedCredentialCheckpoint<string>();
credential.LoadVerified("verified-old-account");
credential.Stage("other-account-oauth-candidate");
Assert.That(credential.IsPending && credential.Active == "other-account-oauth-candidate",
    "OAuth candidate available for account validation without disk commit");
credential.Reject();
Assert.That(!credential.IsPending && credential.Active == "verified-old-account",
    "mismatched Google account keeps previously verified credentials");
credential.Stage("refreshed-token-1");
credential.Stage("refreshed-token-2");
credential.Reject();
Assert.That(credential.Active == "verified-old-account",
    "multiple pending refresh attempts do not lose previously verified token");
credential.Stage("refreshed-token-3");
try
{
    credential.PersistVerified(_ => throw new IOException("simulated token disk failure"));
    throw new Exception("FAIL: failed credential checkpoint did not propagate");
}
catch (IOException) { Console.WriteLine("PASS: failed verified credential checkpoint is detected"); }
Assert.That(credential.IsPending && credential.Active == "refreshed-token-3",
    "credential remains pending until token file is durable");
credential.Reject();
Assert.That(credential.Active == "verified-old-account" && !credential.IsPending,
    "credential can return to durable version after disk error");
var persistedTokens = new List<string>();
credential.Stage("verified-refreshed-account-token");
credential.PersistVerified(persistedTokens.Add);
Assert.That(persistedTokens.SequenceEqual(["verified-refreshed-account-token"])
    && credential.Active == "verified-refreshed-account-token" && !credential.IsPending,
    "verified credential becomes live and durable at the same checkpoint");
credential.PersistVerified(persistedTokens.Add);
Assert.That(persistedTokens.Count == 1, "verified credentials are not written again without a new candidate");
var emptyCredential = new VerifiedCredentialCheckpoint<string>();
emptyCredential.Stage("first-login-to-wrong-account");
emptyCredential.Reject();
Assert.That(emptyCredential.Active is null, "new-account rejection never creates a stored credential");

// v10: a feature toggle must never commit its live state before a durable save.
var deckEnabled = true;
var writes = 0;
var switchErrors = 0;
var switchResult = FeatureSwitchCheckpoint.Apply(
    () => deckEnabled, value => deckEnabled = value, false,
    () => { writes++; throw new IOException("simulated settings save failure"); },
    _ => switchErrors++);
Assert.That(switchResult == FeatureSwitchCheckpoint.Outcome.SaveFailed
    && deckEnabled && writes == 1 && switchErrors == 1,
    "failed feature-toggle checkpoint restores previous ON state");
switchResult = FeatureSwitchCheckpoint.Apply(
    () => deckEnabled, value => deckEnabled = value, true,
    () => writes++, _ => switchErrors++);
Assert.That(switchResult == FeatureSwitchCheckpoint.Outcome.Unchanged && writes == 1,
    "unchanged feature preference requires no disk write");
switchResult = FeatureSwitchCheckpoint.Apply(
    () => deckEnabled, value => deckEnabled = value, false,
    () => writes++, _ => switchErrors++);
Assert.That(switchResult == FeatureSwitchCheckpoint.Outcome.Applied && !deckEnabled && writes == 2,
    "saved feature-toggle checkpoint commits OFF state");

// Built-in Korean holiday subscriptions are standalone, read-only ICS feeds.
var publicCalendars = KoreanHolidayCalendars.All;
Assert.That(publicCalendars.Count == 2
    && publicCalendars[0].Id == KoreanHolidayCalendars.AppleId
    && publicCalendars[1].Id == KoreanHolidayCalendars.GoogleId,
    "Korean holiday calendars have stable IDs in Apple/Google order");
var oldHolidayPreferences = JsonSerializer.Deserialize<CalendarPreferences>("{\"Catalog\":[],\"SelectedCalendarIds\":[]}")!;
Assert.That(oldHolidayPreferences.ShowAppleKoreanHolidays && !oldHolidayPreferences.ShowGoogleKoreanHolidays,
    "legacy preferences default to Apple-on and Google-off without changing Google selection");
var holidayTemp = Path.Combine(Path.GetTempPath(), "cyandeck-holiday-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(holidayTemp);
try
{
    var store = new CalendarPreferencesStore(Path.Combine(holidayTemp, "preferences.json"));
    store.SetVisibleCalendars([]);
    Assert.That(store.IsVisible(KoreanHolidayCalendars.AppleId)
        && !store.IsVisible(KoreanHolidayCalendars.GoogleId)
        && !store.IsVisible("work@gmail.com"),
        "built-in holidays do not depend on Google account selection");
    store.SetHolidayVisibility(KoreanHolidayCalendars.AppleId, false);
    store.SetHolidayVisibility(KoreanHolidayCalendars.GoogleId, true);
    var reopened = new CalendarPreferencesStore(Path.Combine(holidayTemp, "preferences.json"));
    Assert.That(!reopened.IsVisible(KoreanHolidayCalendars.AppleId)
        && reopened.IsVisible(KoreanHolidayCalendars.GoogleId)
        && reopened.Data.SelectedCalendarIds is { Count: 0 },
        "holiday toggles are durably saved without changing Google selection");
    Assert.That(WeekBarLayout.Build(new DateTime(2026, 10, 4), KoreanHolidayIcsParser.Parse(
        "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:test\nDTSTART;VALUE=DATE:20261009\nDTEND;VALUE=DATE:20261011\nSUMMARY:연휴\nEND:VEVENT\nEND:VCALENDAR",
        KoreanHolidayCalendars.AppleId, new DateTime(2026, 10, 9))).Single().ColumnSpan == 2,
        "ICS all-day exclusive end is converted to inclusive multi-day bars");
    const string holidayFixture = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:repeat\r\n"
        + "DTSTART;VALUE=DATE:20240101\r\nRRULE:FREQ=YEARLY;COUNT=5\r\n"
        + "EXDATE;VALUE=DATE:20260101\r\nSUMMARY:새해\\,공휴일\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:fold\r\nDTSTART;VALUE=DATE:20261009\r\n"
        + "SUMMARY:한글 공휴\r\n 일\r\nEND:VEVENT\r\nEND:VCALENDAR";
    var imported = KoreanHolidayIcsParser.Parse(holidayFixture, KoreanHolidayCalendars.AppleId,
        new DateTime(2026, 10, 9));
    Assert.That(imported.Any(x => x.Date == new DateTime(2027, 1, 1))
        && !imported.Any(x => x.Date == new DateTime(2026, 1, 1))
        && imported.Any(x => x.Title == "한글 공휴일")
        && imported.All(x => x.RemoteReadOnly && !x.Dirty
            && x.CalendarId == KoreanHolidayCalendars.AppleId),
        "public ICS parsing unfolds Korean text, expands yearly RRULE, applies EXDATE and marks events readonly");
    Assert.That(KoreanHolidayIcsParser.Parse(holidayFixture, KoreanHolidayCalendars.AppleId,
        new DateTime(2026, 10, 9)).Select(x => x.Id).SequenceEqual(imported.Select(x => x.Id)),
        "ICS event IDs remain stable across refreshes");
    var denied = false;
    try { new CalendarEventStore(Path.Combine(holidayTemp, "events.json")).UpsertBatch(
        CalendarEventMovePolicy.CreateDraft(imported.First(), new DateTime(2026, 10, 12)), imported.First()); }
    catch (InvalidOperationException) { denied = true; }
    Assert.That(denied, "imported holiday events cannot be edited through the existing event store");
    store.SetColorOverride(KoreanHolidayCalendars.AppleId, "#19AABB");
    store.SetColorOverride(KoreanHolidayCalendars.GoogleId, "#CDEF12");
    var colorSaved = new CalendarPreferencesStore(Path.Combine(holidayTemp, "preferences.json"));
    Assert.That(colorSaved.ColorFor(KoreanHolidayCalendars.AppleId) == "#19AABB"
        && colorSaved.ColorFor(KoreanHolidayCalendars.GoogleId) == "#CDEF12"
        && colorSaved.Data.SelectedCalendarIds is { Count: 0 },
        "holiday color overrides persist independently of Google account selection");
    colorSaved.SetColorOverride(KoreanHolidayCalendars.AppleId, null);
    Assert.That(new CalendarPreferencesStore(Path.Combine(holidayTemp, "preferences.json"))
        .ColorFor(KoreanHolidayCalendars.AppleId) == "#F83A22",
        "Apple holiday color resets to default red");

    const string downloadedIcs = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:net\r\n"
        + "DTSTART;VALUE=DATE:20261011\r\nSUMMARY:공휴일 테스트\r\nEND:VEVENT\r\nEND:VCALENDAR";
    var countRequests = 0;
    using (var client = new HttpClient(new HolidayStubHandler(uri =>
    {
        countRequests++;
        return uri.AbsoluteUri.Contains("icloud.com", StringComparison.OrdinalIgnoreCase)
            ? "<html>upstream server error</html>" : downloadedIcs;
    })))
    using (var holidayService = new KoreanHolidayFeedService(holidayTemp, client))
    {
        var result = holidayService.RefreshAsync([KoreanHolidayCalendars.AppleId],
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.That(result.Count == 1 && result[0].Success && result[0].UsedFallback
            && result[0].EventCount == 1 && countRequests == 2
            && holidayService.Events.Single().RemoteReadOnly,
            "public holiday fallback survives a non-ICS primary response");
        Assert.That(KoreanHolidayFeedService.DescribeResults(result).Contains("대체 주소 사용"),
            "refresh UI reports completed fallback rather than staying busy");
        Assert.That(File.Exists(Path.Combine(holidayTemp, "holidays-apple-kr.ics")),
            "fallback result becomes durable cache");
    }
    var googleRequests = new List<string>();
    using (var googleClient = new HttpClient(new HolidayStubHandler(uri =>
    {
        googleRequests.Add(uri.AbsoluteUri);
        return uri.Host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase)
            ? downloadedIcs : "<html>Google ICS temporarily unavailable</html>";
    })))
    using (var googleService = new KoreanHolidayFeedService(holidayTemp, googleClient))
    {
        var result = googleService.RefreshAsync([KoreanHolidayCalendars.GoogleId],
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.That(result.Count == 1 && result[0].Success && result[0].UsedFallback
            && result[0].EventCount == 1 && googleRequests.Count == 2
            && googleRequests[0].Contains("ko.south_korea.official%23holiday%40group.v.calendar.google.com")
            && googleRequests[1].Contains("www.google.com")
            && googleService.Events.Count(e => e.CalendarId == KoreanHolidayCalendars.GoogleId) == 1,
            "Google holidays retry same calendar ID on alternate host after non-ICS response");
        Assert.That(KoreanHolidayFeedService.DescribeResults(result).Contains("대체 주소 사용"),
            "Google holiday fallback returns completed result to settings instead of loading forever");
    }
    var googleAlternates = new List<string>();
    using (var alternateClient = new HttpClient(new HolidayStubHandler(uri =>
    {
        googleAlternates.Add(uri.AbsoluteUri);
        return uri.AbsoluteUri.Contains("ko.south_korea%23holiday%40group", StringComparison.OrdinalIgnoreCase)
            ? downloadedIcs : "<html>not available</html>";
    })))
    using (var alternateService = new KoreanHolidayFeedService(holidayTemp, alternateClient))
    {
        var result = alternateService.RefreshAsync([KoreanHolidayCalendars.GoogleId],
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.That(result.Count == 1 && result[0].Success && result[0].UsedFallback
            && googleAlternates.Count == 3,
            "Google holiday source tries alternate Korean Google feed if both same-ID hosts fail");
    }
    using (var badClient = new HttpClient(new HolidayStubHandler(_ => "<html>not ICS</html>")))
    using (var offlineService = new KoreanHolidayFeedService(holidayTemp, badClient))
    {
        var result = offlineService.RefreshAsync([KoreanHolidayCalendars.AppleId],
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.That(result.Count == 1 && !result[0].Success && result[0].EventCount == 1
            && offlineService.Events.Count(e => e.CalendarId == KoreanHolidayCalendars.AppleId) == 1,
            "failed public holiday refresh retains last-good cached data");
        Assert.That(KoreanHolidayFeedService.DescribeResults(result).Contains("가져오기 실패")
            && KoreanHolidayFeedService.DescribeResults(result).Contains("캐시 유지"),
            "refresh UI shows failure and cached count instead of indefinite progress");
    }
}
finally { Directory.Delete(holidayTemp, recursive: true); }

// Explicit local-only events must survive restarts, edits and long-press relocation
// without being queued for Google upload (and without requiring an account).
var localOnlyScratch = Path.Combine(Path.GetTempPath(), "cyandeck-local-only-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(localOnlyScratch);
try
{
    var localPath = Path.Combine(localOnlyScratch, "calendar-events.json");
    var local = new CalendarEventStore(localPath);
    Assert.That(local.UpsertBatch(new CalendarEventDraft
    {
        StartDate = new DateTime(2026, 10, 12), EndDate = new DateTime(2026, 10, 12),
        CalendarId = CalendarEventStore.LocalCalendarId, LocalOnly = true,
        Text = "local first\nlocal second"
    }) == 2, "local-only multiline draft persists without a Google catalog");
    Assert.That(local.Events.All(e => e.LocalOnly && !e.Dirty && e.RemoteId is null
        && e.CalendarId == CalendarEventStore.LocalCalendarId),
        "local-only events are never pending a remote upload");
    var reopened = new CalendarEventStore(localPath);
    Assert.That(reopened.Events.Count == 2 && reopened.Events.All(e => e.LocalOnly),
        "local-only event flag survives a JSON round trip");
    var localFirst = reopened.Events[0];
    reopened.UpsertBatch(CalendarEventMovePolicy.CreateDraft(localFirst,
        new DateTime(2026, 10, 15)), localFirst);
    Assert.That(reopened.Events[0].LocalOnly && !reopened.Events[0].Dirty
        && reopened.Events[0].Date == new DateTime(2026, 10, 15),
        "drag-and-drop preserves local-only flag and leaves sync queue clean");
    var localSecond = reopened.Events[1];
    reopened.UpsertBatch(new CalendarEventDraft
    {
        StartDate = localSecond.Date, EndDate = localSecond.Date, CalendarId = "primary",
        LocalOnly = false, Text = localSecond.Title
    }, localSecond);
    Assert.That(reopened.Events[1].Dirty && !reopened.Events[1].LocalOnly
        && reopened.Events[1].CalendarId == "primary",
        "unsynced local-only event can explicitly be switched to Google upload");
    var synced = new CalendarEvent { Title = "remote", CalendarId = "primary",
        RemoteId = "g-001", RemoteETag = "etag", Dirty = false };
    reopened.CommitSnapshot([synced]);
    var rejected = false;
    try
    {
        reopened.UpsertBatch(new CalendarEventDraft
        {
            StartDate = synced.Date, EndDate = synced.Date,
            CalendarId = CalendarEventStore.LocalCalendarId,
            LocalOnly = true, Text = synced.Title
        }, reopened.Events.Single());
    }
    catch (InvalidOperationException) { rejected = true; }
    Assert.That(rejected && reopened.Events.Single().RemoteId == "g-001",
        "Google-linked event cannot silently become local-only and strand a remote copy");
}
finally { Directory.Delete(localOnlyScratch, recursive: true); }

// Update channels: stable does not consume prereleases; opt-in beta can choose
// the highest published numeric CyanDeck release and ignores draft versions.
using (var releases = System.Text.Json.JsonDocument.Parse("""
[
  {"tag_name":"v8.0.2","draft":false,"prerelease":false},
  {"tag_name":"v8.0.4","draft":true,"prerelease":true},
  {"tag_name":"v8.0.3","draft":false,"prerelease":true},
  {"tag_name":"v7.49.5","draft":false,"prerelease":false}
]
"""))
{
    Assert.That(GitHubReleaseSource.SelectLatestBetaReleaseTag(releases.RootElement) == "v8.0.3",
        "beta includes published prereleases but not drafts or old product");
}
Assert.That(GitHubReleaseSource.PackageSource(GitHubReleaseSource.BetaChannel, "CyanDeck-8.0.3-win-x64.zip")
    == "github-release-beta://deadpixel134/CyanDeck/CyanDeck-8.0.3-win-x64.zip",
    "beta package source retains its channel identity");


// Per-event appearance is a separate, durable local-only visual preference.
// This works for Google, local and deterministic holiday event IDs and never
// marks remote events Dirty or modifies calendar-events.json.
var appearanceScratch = Path.Combine(Path.GetTempPath(),
    "cyandeck-event-appearance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(appearanceScratch);
try
{
    var stylePath = Path.Combine(appearanceScratch, "calendar-event-appearance.json");
    var eventPath = Path.Combine(appearanceScratch, "calendar-events.json");
    var originalCalendar = new CalendarEventStore(eventPath);
    var linked = new CalendarEvent { Title = "Remote untouched", CalendarId = "primary",
        RemoteId = "remote-17", RemoteETag = "v1", Dirty = false, RemoteReadOnly = true };
    originalCalendar.CommitSnapshot([linked]);
    var originalEventJson = File.ReadAllText(eventPath);
    var appearance = new CalendarEventAppearanceStore(stylePath);
    Assert.That(appearance.Get(linked.Id) == new CalendarEventAppearance(),
        "events default to per-calendar background and white text");
    appearance.Set(linked.Id, "#a1b2c3", "#101318");
    Assert.That(appearance.Get(linked.Id) is { BackgroundColor: "#A1B2C3", TextColor: "#101318" },
        "event background and font colors persist independently");
    var holidayId = Guid.NewGuid();
    appearance.Set(holidayId, null, "#FFFFFF");
    var reopenedStyle = new CalendarEventAppearanceStore(stylePath);
    Assert.That(reopenedStyle.Get(linked.Id).BackgroundColor == "#A1B2C3" &&
        reopenedStyle.Get(holidayId).TextColor == "#FFFFFF",
        "Google and holiday event styles survive restart with stable IDs");
    Assert.That(File.ReadAllText(eventPath) == originalEventJson &&
        originalCalendar.Events.Single().Dirty == false,
        "visual-only event changes never change Google sync or event store");
    Assert.That(!CalendarEventAppearanceStore.IsValid("#GGG000") &&
        !CalendarEventAppearanceStore.IsValid("#FFF") &&
        CalendarEventAppearanceStore.IsValid("#2f3A6b"),
        "invalid colors are rejected");
    var invalidColor = false;
    try { reopenedStyle.Set(holidayId, "#G00000", null); }
    catch (ArgumentException) { invalidColor = true; }
    Assert.That(invalidColor && reopenedStyle.Get(holidayId).TextColor == "#FFFFFF",
        "invalid color leaves existing durable style untouched");
    reopenedStyle.Set(linked.Id, null, "#F83A22");
    Assert.That(new CalendarEventAppearanceStore(stylePath).Get(linked.Id) is
        { BackgroundColor: null, TextColor: "#F83A22" },
        "independent background reset retains text color");
    reopenedStyle.Set(linked.Id, null, null);
    Assert.That(new CalendarEventAppearanceStore(stylePath).Get(linked.Id) ==
        new CalendarEventAppearance(), "both appearance colors restore defaults");
    // Removing the primary must never overwrite or auto-import the backup.
    File.Delete(stylePath);
    var protectedStyle = new CalendarEventAppearanceStore(stylePath);
    Assert.That(protectedStyle.IsReadOnly && protectedStyle.IsRecoveredFromBackup,
        "missing style primary loads backup read-only and preserves recovery");
    Assert.StorageFailure(() => protectedStyle.Set(holidayId, "#FFFFFF", null),
        "appearance backup safety blocks writes until explicit recovery");
    Assert.That(!File.Exists(stylePath),
        "recovered style must not silently create replacement primary");
}
finally { Directory.Delete(appearanceScratch, recursive: true); }

// v68: one rejected Google event must not stall the whole sync; sign-in expiry is
// recognised; prompt upload never opens a login; disconnect clears remote identity;
// local-only events can be backed up and imported without duplicating Google events.
{
    const string v68OrganizerOnly = "{\"error\":{\"errors\":[{\"domain\":\"calendar\",\"reason\":\"forbiddenForNonOrganizer\",\"message\":\"x\"}],\"code\":403,\"message\":\"x\"}}";
    const string v68ScopeMissing = "{\"error\":{\"code\":403,\"message\":\"x\",\"status\":\"PERMISSION_DENIED\",\"details\":[{\"reason\":\"ACCESS_TOKEN_SCOPE_INSUFFICIENT\"}]}}";
    Assert.That(GoogleSyncFailurePolicy.IsEventRejection(403, v68OrganizerOnly) &&
        GoogleSyncFailurePolicy.DescribeRejection(403, v68OrganizerOnly) ==
            GoogleSyncFailurePolicy.RejectionPrefix + GoogleSyncFailurePolicy.OrganizerOnlyDetail,
        "organizer-only 403 rejects just that event with a Korean reason");
    Assert.That(!GoogleSyncFailurePolicy.IsEventRejection(403, v68ScopeMissing),
        "account-wide 403 (missing scope) still stops the whole sync");
    Assert.That(!GoogleSyncFailurePolicy.IsPermanentEventRejection(500) &&
        !GoogleSyncFailurePolicy.IsPermanentEventRejection(401) &&
        !GoogleSyncFailurePolicy.IsPermanentEventRejection(429) &&
        GoogleSyncFailurePolicy.IsPermanentEventRejection(400) &&
        GoogleSyncFailurePolicy.IsPermanentEventRejection(404),
        "network, server, sign-in and quota errors are not per-event rejections");
    Assert.That(GoogleSyncFailurePolicy.DescribeRejection(404, "not json").Contains("찾을 수 없습니다"),
        "unparseable 404 still gets a readable reason");
    Assert.That(GoogleSyncFailurePolicy.IsReauthorizationRequired(400,
            "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}") &&
        !GoogleSyncFailurePolicy.IsReauthorizationRequired(500, "{\"error\":\"invalid_grant\"}") &&
        !GoogleSyncFailurePolicy.IsReauthorizationRequired(400, "{\"error\":\"invalid_client\"}"),
        "only an explicit invalid_grant requires signing in again");

    static bool V68Invited(string json, string calendarId)
    {
        using var document = JsonDocument.Parse(json);
        return GoogleSyncFailurePolicy.IsInvitedAttendeeCopy(document.RootElement, calendarId);
    }
    Assert.That(V68Invited("{\"organizer\":{\"email\":\"boss@example.com\"},\"attendees\":[{\"email\":\"me@example.com\",\"self\":true}]}", "me@example.com"),
        "a meeting organized by someone else is read-only from download");
    Assert.That(!V68Invited("{\"guestsCanModify\":true,\"organizer\":{\"email\":\"boss@example.com\"},\"attendees\":[{\"self\":true}]}", "me@example.com") &&
        !V68Invited("{\"organizer\":{\"email\":\"me@example.com\",\"self\":true},\"attendees\":[{\"self\":true}]}", "me@example.com") &&
        !V68Invited("{\"organizer\":{\"email\":\"team@group.calendar.google.com\"},\"attendees\":[{\"self\":true}]}", "team@group.calendar.google.com") &&
        !V68Invited("{\"organizer\":{\"email\":\"boss@example.com\"}}", "me@example.com"),
        "own events, guest-editable meetings and shared-calendar events stay editable");

    Assert.That(CalendarPromptSyncPolicy.Decide(true, true, false, false, false, false) == CalendarPromptSyncDecision.Run &&
        CalendarPromptSyncPolicy.Decide(false, true, false, false, false, false) == CalendarPromptSyncDecision.Skip &&
        CalendarPromptSyncPolicy.Decide(true, false, false, false, false, false) == CalendarPromptSyncDecision.Skip &&
        CalendarPromptSyncPolicy.Decide(true, true, false, true, false, false) == CalendarPromptSyncDecision.RetryLater &&
        CalendarPromptSyncPolicy.Decide(true, true, false, false, true, false) == CalendarPromptSyncDecision.RetryLater &&
        CalendarPromptSyncPolicy.Decide(true, true, false, false, false, true) == CalendarPromptSyncDecision.RetryLater,
        "prompt upload runs only with a silent token and waits for edits, drags and running syncs");

    var v68Nine = new DateTime(2026, 3, 2, 9, 0, 0);
    var v68Pending = new CalendarEvent { Title = "회의", Date = v68Nine.Date, EndDate = v68Nine.Date,
        StartAt = new DateTimeOffset(v68Nine, TimeZoneInfo.Local.GetUtcOffset(v68Nine)),
        EndAt = new DateTimeOffset(v68Nine.AddMinutes(90), TimeZoneInfo.Local.GetUtcOffset(v68Nine.AddMinutes(90))),
        CalendarId = "primary", Dirty = true };
    var v68Tooltip = CalendarEventStatusText.Tooltip(v68Pending, "내 캘린더");
    Assert.That(CalendarEventStatusText.BarPrefix(v68Pending) == CalendarEventStatusText.PendingMarker &&
        v68Tooltip.Contains("09:00~10:30") && v68Tooltip.Contains("내 캘린더 · Google 업로드 대기"),
        "pending upload is marked on the bar and the tooltip shows time, calendar and state");
    Assert.That(CalendarEventStatusText.BarPrefix(v68Pending, googleConnected: false) == "" &&
        CalendarEventStatusText.SyncState(v68Pending, googleConnected: false) == "로컬 (Google 연결 전)",
        "events saved before any Google connection show no upload marker");
    var v68DeletedLinked = new CalendarEvent { Title = "삭제", RemoteId = "g4", RemoteETag = "e4",
        Dirty = true, Deleted = true, CalendarId = "primary" };
    Assert.That(CalendarPromptSyncPolicy.HasPendingUploads(new[] { v68DeletedLinked }) &&
        CalendarEventStatusText.BarPrefix(v68DeletedLinked) == "" &&
        !CalendarPromptSyncPolicy.HasPendingUploads(new[] { new CalendarEvent { Title = "x", LocalOnly = true, Dirty = true } }),
        "a deleted Google event counts as upload work for the prompt sync");
    var v68Rejected = new CalendarEvent { Title = "초대", Conflict = true, Dirty = true, RemoteId = "r1",
        SyncError = GoogleSyncFailurePolicy.RejectionPrefix + GoogleSyncFailurePolicy.OrganizerOnlyDetail };
    var v68LocalOnly = new CalendarEvent { Title = "개인", LocalOnly = true, Dirty = false,
        CalendarId = CalendarEventStore.LocalCalendarId };
    Assert.That(CalendarEventStatusText.BarPrefix(v68Rejected) == CalendarEventStatusText.ConflictMarker &&
        CalendarEventStatusText.SyncState(v68Rejected).Contains(GoogleSyncFailurePolicy.OrganizerOnlyDetail) &&
        CalendarEventStatusText.BarPrefix(v68LocalOnly) == "" &&
        CalendarEventStatusText.SyncState(v68LocalOnly) == "로컬 전용",
        "conflict shows its reason and local-only events show no upload marker");

    var v68Events = new List<CalendarEvent>
    {
        v68LocalOnly,
        new() { Title = "clean google", RemoteId = "g1", RemoteETag = "e1", Dirty = false, CalendarId = "me@example.com" },
        new() { Title = "unsent edit", RemoteId = "g2", RemoteETag = "e2", Dirty = true, CalendarId = "me@example.com" },
        new() { Title = "pending create", RemoteId = "ddabc", RemoteCreatePending = true, Dirty = true, CalendarId = "me@example.com" },
        new() { Title = "never uploaded", Dirty = true, CalendarId = "me@example.com" },
        new() { Title = "pending delete", RemoteId = "g3", RemoteETag = "e3", Dirty = true, Deleted = true, CalendarId = "me@example.com" }
    };
    Assert.That(GoogleDisconnectPolicy.CountUnsentChanges(v68Events) == 3 &&
        GoogleDisconnectPolicy.CountDownloadedCopies(v68Events) == 1,
        "disconnect warning counts unsent changes and downloaded copies");
    var v68Disconnect = GoogleDisconnectPolicy.Apply(v68Events);
    Assert.That(v68Disconnect.Removed == 2 && v68Disconnect.ConvertedToLocal == 3 && v68Disconnect.KeptLocal == 1 &&
        v68Events.Count == 4 &&
        v68Events.All(e => e.LocalOnly && e.RemoteId is null && e.RemoteETag is null && !e.RemoteCreatePending && !e.Dirty &&
            e.CalendarId == CalendarEventStore.LocalCalendarId),
        "disconnect leaves no event that could be pushed to a different account");

    var v68Credentials = new VerifiedCredentialCheckpoint<string>();
    v68Credentials.LoadVerified("old");
    v68Credentials.Stage("candidate");
    v68Credentials.Clear();
    Assert.That(v68Credentials.Active is null && !v68Credentials.IsPending,
        "disconnect forgets verified and candidate credentials");

    var v68Scratch = Path.Combine(Path.GetTempPath(), "cyandeck-v68-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(v68Scratch);
    try
    {
        var v68PrefsPath = Path.Combine(v68Scratch, "calendar-preferences.json");
        var v68Prefs = new CalendarPreferencesStore(v68PrefsPath);
        v68Prefs.UpdateCatalog([new GoogleCalendarSource { Id = "me@example.com", Name = "Me", AccessRole = "owner", IsPrimary = true }]);
        v68Prefs.UpdateSyncToken("me@example.com", "sync-1");
        v68Prefs.SetHolidayVisibility(KoreanHolidayCalendars.GoogleId, true);
        v68Prefs.ClearGoogleAccount();
        var v68Reopened = new CalendarPreferencesStore(v68PrefsPath).Data;
        Assert.That(v68Reopened.Catalog.Count == 0 && v68Reopened.SyncTokens.Count == 0 &&
            v68Reopened.SelectedCalendarIds is null && v68Reopened.TargetCalendarId is null &&
            v68Reopened.ShowGoogleKoreanHolidays,
            "disconnect clears account catalog, tokens and selection but keeps holiday choices");

        var v68StorePath = Path.Combine(v68Scratch, "calendar-events.json");
        var v68Store = new CalendarEventStore(v68StorePath);
        var v68Local = new CalendarEvent { Title = "로컬 일정", Date = new DateTime(2026, 5, 1), EndDate = new DateTime(2026, 5, 2),
            LocalOnly = true, Dirty = false, CalendarId = CalendarEventStore.LocalCalendarId };
        var v68Linked = new CalendarEvent { Title = "구글 일정", Date = new DateTime(2026, 5, 3), RemoteId = "g9",
            RemoteETag = "e9", Dirty = false, CalendarId = "me@example.com", SyncError = "Google이 수정을 거부함: test" };
        v68Store.CommitSnapshot([v68Local, v68Linked]);
        Assert.That(new CalendarEventStore(v68StorePath).Events.Single(e => e.RemoteId == "g9").SyncError == "Google이 수정을 거부함: test" &&
            v68Store.CreateSnapshot().Single(e => e.RemoteId == "g9").SyncError == "Google이 수정을 거부함: test",
            "rejection reason survives restart and snapshot copies");

        var v68BackupPath = Path.Combine(v68Scratch, "backup.json");
        var v68Styles = new CalendarEventAppearanceStore(Path.Combine(v68Scratch, "calendar-event-appearance.json"));
        v68Styles.Set(v68Local.Id, "#26B7A9", null);
        Assert.That(CalendarEventBackup.Export(v68Store.Events, v68Styles.Get, v68BackupPath) == 1,
            "only local events are exported");
        var v68Backup = CalendarEventBackup.Read(v68BackupPath);
        Assert.That(v68Backup.Events.Single().Title == "로컬 일정" &&
            v68Backup.Appearance[v68Local.Id.ToString("N")].BackgroundColor == "#26B7A9",
            "backup keeps the event and its color");
        Assert.That(CalendarEventBackup.PlanImport(v68Store.Events, v68Backup) is { Added.Count: 0, SkippedDuplicates: 1 },
            "importing into the same calendar adds no duplicate");

        var v68Fresh = new CalendarEventStore(Path.Combine(v68Scratch, "fresh-events.json"));
        v68Backup.Events.Add(new CalendarEvent { Title = "구글 사본", RemoteId = "g10", Dirty = false });
        var v68Plan = CalendarEventBackup.PlanImport(v68Fresh.Events, v68Backup);
        Assert.That(v68Plan.Added.Count == 1 && v68Plan.SkippedLinked == 1 &&
            v68Plan.Added[0] is { LocalOnly: true, Dirty: false, RemoteId: null } &&
            v68Plan.Added[0].CalendarId == CalendarEventStore.LocalCalendarId &&
            v68Plan.Appearance[v68Plan.Added[0].Id].BackgroundColor == "#26B7A9",
            "imported events become local-only, Google copies are skipped and colors follow");
        var v68FreshStyles = new CalendarEventAppearanceStore(Path.Combine(v68Scratch, "fresh-appearance.json"));
        v68FreshStyles.SetMany(v68Plan.Appearance);
        Assert.That(new CalendarEventAppearanceStore(Path.Combine(v68Scratch, "fresh-appearance.json"))
                .Get(v68Plan.Added[0].Id).BackgroundColor == "#26B7A9",
            "imported colors are saved in one checkpoint");

        var v68NotBackup = Path.Combine(v68Scratch, "layout-export.json");
        File.WriteAllText(v68NotBackup, "{\"Settings\":{},\"Fences\":[]}");
        var v68Rejects = false;
        try { CalendarEventBackup.Read(v68NotBackup); }
        catch (InvalidDataException) { v68Rejects = true; }
        Assert.That(v68Rejects, "a layout export is not mistaken for a calendar backup");
        Assert.That(CalendarEventBackup.CompanionPathFor(Path.Combine(v68Scratch, "CyanDeck-backup-1.json"))
                == Path.Combine(v68Scratch, "CyanDeck-backup-1.calendar-events.json"),
            "layout export companion file sits next to the export");

        var v68Unconfirmed = new CalendarEvent { Title = "올리지 못한 일정", Date = new DateTime(2026, 5, 5),
            RemoteId = "dd" + Guid.NewGuid().ToString("N"), RemoteCreatePending = true, Dirty = true, CalendarId = "me@example.com" };
        var v68UnconfirmedPath = Path.Combine(v68Scratch, "unconfirmed.json");
        Assert.That(CalendarEventBackup.IsLocalEvent(v68Unconfirmed) &&
            CalendarEventBackup.Export(new[] { v68Unconfirmed }, _ => new CalendarEventAppearance(), v68UnconfirmedPath) == 1 &&
            CalendarEventBackup.Read(v68UnconfirmedPath).Events.Single().RemoteId is null,
            "a create Google never confirmed is backed up as a local event");

        var v68LayoutPath = Path.Combine(v68Scratch, "CyanDeck-backup-2.json");
        var v68CompanionPath = CalendarEventBackup.CompanionPathFor(v68LayoutPath);
        CalendarEventBackup.Export(new[] { v68Local }, _ => new CalendarEventAppearance(), v68CompanionPath);
        Assert.That(CalendarEventBackup.ExportCompanion(new[] { v68Linked }, v68LayoutPath) == 0 &&
            CalendarEventBackup.Read(v68CompanionPath).Events.Count == 0,
            "an older companion is emptied when the new export has no local events");
    }
    finally { Directory.Delete(v68Scratch, recursive: true); }
}

Console.WriteLine("All calendar domain tests passed.");

static class Assert
{
    public static void That(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
    // File access failures are distinct from content validation failures.
    // InvalidDataException does NOT inherit IOException on .NET 8.
    public static void StorageFailure(Action action, string label)
    {
        try { action(); }
        catch (IOException) { Console.WriteLine("PASS: " + label); return; }
        catch (UnauthorizedAccessException) { Console.WriteLine("PASS: " + label); return; }
        throw new Exception("FAIL: " + label);
    }
    public static void InvalidBackupFailure(Action action, string expectedMessage, string label)
    {
        try { action(); }
        catch (InvalidDataException ex)
        {
            if (!string.Equals(ex.Message, expectedMessage, StringComparison.Ordinal))
                throw new Exception("FAIL: unexpected backup validation error: " + label, ex);
            Console.WriteLine("PASS: " + label);
            return;
        }
        throw new Exception("FAIL: expected InvalidDataException: " + label);
    }
}


namespace CyanDeck.Services
{
    public static class DiagnosticsLog
    {
        public static void Error(string message, Exception exception) { }
        public static void Warn(string message) { }
    }
}

sealed class HolidayStubHandler : HttpMessageHandler
{
    private readonly Func<Uri, string> _reply;
    public HolidayStubHandler(Func<Uri, string> reply) => _reply = reply;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_reply(request.RequestUri!))
        });
}

