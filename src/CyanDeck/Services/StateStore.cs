using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CyanDeck.Models;
using CyanDeck.Appearance;

namespace CyanDeck.Services;

public sealed class StateStore
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _path;
    private readonly string _backupPath;
    private readonly JsonSerializerOptions _json;

    public bool EncounteredUnsupportedState { get; private set; }
    public int UnsupportedStateVersion { get; private set; }
    // v8.0.44: where the last Load() found the layout. Unreadable means layout files exist but
    // none could be read: callers must not create/save a default layout over them.
    public LayoutLoadOutcome LastLoadOutcome { get; private set; } = LayoutLoadOutcome.NoLayout;
    public bool EncounteredUnreadableState => LastLoadOutcome == LayoutLoadOutcome.Unreadable;
    public bool LoadedFromBackup => LastLoadOutcome is LayoutLoadOutcome.Backup or LayoutLoadOutcome.Staged;
    // Folder that keeps copies of the unreadable files (null when nothing could be copied).
    public string? UnreadableArchiveFolder { get; private set; }
    private bool _lastTryLoadIoFailure;
    // Auto-save prepares its payload on the UI thread and commits it in the background. A
    // prepared payload older than an already committed one must never overwrite it.
    private long _saveSequence;
    private long _committedSequence;

    public StateStore()
    {
        _dir = DataRootPaths.ActiveRoaming;
        _path = Path.Combine(_dir, "layout.json");
        _backupPath = _path + ".bak";
        _json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public DeckState Load() => Load(archiveUnreadable: true);

    public DeckState Load(bool archiveUnreadable)
    {
        lock (_gate)
        {
            EncounteredUnsupportedState = false;
            UnsupportedStateVersion = 0;
            LastLoadOutcome = LayoutLoadOutcome.NoLayout;
            UnreadableArchiveFolder = null;
            // A sharing violation may leave a fully flushed, newer layout.json.tmp.
            // Recover it first on the next launch; if incomplete or invalid, keep
            // the existing primary/backup recovery order without deleting either.
            var stagedPath = _path + ".tmp";
            DeckState? state = null;
            if (HasNewerStagedLayout(stagedPath))
            {
                state = TryLoad(stagedPath);
                if (state is not null)
                {
                    LastLoadOutcome = LayoutLoadOutcome.StagedNewer;
                    DiagnosticsLog.Warn("Recovered newer staged layout after interrupted/locked save; primary layout and backup retained.");
                }
            }
            // v8.0.44: a backup/sync/antivirus lock is often brief, so retry a locked file a
            // few times before falling back to an older copy.
            if (state is null && (state = TryLoadWithRetry(_path)) is not null)
                LastLoadOutcome = LayoutLoadOutcome.Primary;
            if (state is null && (state = TryLoadWithRetry(_backupPath)) is not null)
                LastLoadOutcome = LayoutLoadOutcome.Backup;
            if (state is null && (state = TryLoad(stagedPath, archiveUnsupported: true)) is not null)
                LastLoadOutcome = LayoutLoadOutcome.Staged;
            if (state is null)
            {
                if (EncounteredUnsupportedState)
                    LastLoadOutcome = LayoutLoadOutcome.UnsupportedFuture;
                else if (File.Exists(_path) || File.Exists(_backupPath) || File.Exists(stagedPath))
                {
                    // Files exist but none could be read: keep copies, never overwrite silently.
                    LastLoadOutcome = LayoutLoadOutcome.Unreadable;
                    if (archiveUnreadable) ArchiveUnreadableLayoutFiles(includeBackup: true);
                    DiagnosticsLog.Warn("Layout files exist but could not be read; starting protected without creating or saving a default layout.");
                }
                state = new DeckState();
            }
            else if (LoadedFromBackup && File.Exists(_path))
            {
                // The primary could not be read: keep a copy before the next save rotates it away.
                if (archiveUnreadable) ArchiveUnreadableLayoutFiles(includeBackup: LastLoadOutcome == LayoutLoadOutcome.Staged);
                DiagnosticsLog.Warn($"Layout loaded from {LastLoadOutcome} copy; recent changes may be missing.");
            }
            Normalize(state);
            return state;
        }
    }

    private DeckState? TryLoadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            _lastTryLoadIoFailure = false;
            var state = TryLoad(path, archiveUnsupported: true);
            if (state is not null || !_lastTryLoadIoFailure || attempt >= 3) return state;
            System.Threading.Thread.Sleep(200 * attempt);
        }
    }

    // Copies (never moves) the layout files that could not be read into
    // unreadable\yyyyMMdd-HHmmss so the next save cannot rotate them away.
    private void ArchiveUnreadableLayoutFiles(bool includeBackup)
    {
        try
        {
            var folder = Path.Combine(_dir, "unreadable", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            var copied = 0;
            var sources = new List<string> { _path, _path + ".tmp", Path.Combine(_dir, "startup-baseline.json") };
            if (includeBackup) sources.Insert(1, _backupPath);
            foreach (var source in sources)
            {
                try
                {
                    if (!File.Exists(source)) continue;
                    File.Copy(source, Path.Combine(folder, Path.GetFileName(source)), overwrite: true);
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticsLog.Warn($"Could not keep a copy of unreadable {Path.GetFileName(source)} (HRESULT 0x{ex.HResult:X8}).");
                }
            }
            UnreadableArchiveFolder = copied > 0 ? folder : null;
            if (copied == 0)
            {
                try { Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsLog.Error("Failed to archive unreadable layout files", ex);
        }
    }

    /// <summary>A normalized empty state, identical to a first start without layout.json.</summary>
    public DeckState CreateDefaultState()
    {
        var state = new DeckState();
        Normalize(state);
        return state;
    }

    /// <summary>
    /// Newest readable recovery source that still contains groups: a recovery checkpoint or the
    /// previous session's start baseline. Used when layout.json and its backup are unreadable.
    /// </summary>
    public string? FindLatestRecoveryCandidate()
    {
        lock (_gate)
        {
            try
            {
                var candidates = new List<FileInfo>();
                var recoveryDir = Path.Combine(_dir, "recovery");
                if (Directory.Exists(recoveryDir)) candidates.AddRange(new DirectoryInfo(recoveryDir).GetFiles("*.json"));
                var baseline = new FileInfo(Path.Combine(_dir, "startup-baseline.json"));
                if (baseline.Exists) candidates.Add(baseline);
                foreach (var file in candidates.Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                             .OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    var state = TryLoad(file.FullName);
                    if (state?.Fences is { Count: > 0 }) return file.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticsLog.Warn($"Recovery candidate search failed (HRESULT 0x{ex.HResult:X8}).");
            }
            return null;
        }
    }

    private bool HasNewerStagedLayout(string stagedPath)
    {
        try
        {
            return File.Exists(stagedPath) &&
                (!File.Exists(_path) || File.GetLastWriteTimeUtc(stagedPath) > File.GetLastWriteTimeUtc(_path));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private DeckState? TryLoad(string path, bool archiveUnsupported = false, bool throwOnUnsupported = false)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var version = ReadSchemaVersion(json);
            if (version > StateSchema.Current)
            {
                if (throwOnUnsupported) throw new UnsupportedStateVersionException(version, path);
                if (archiveUnsupported)
                {
                    ArchiveUnsupportedState(path, version);
                    EncounteredUnsupportedState = true;
                    UnsupportedStateVersion = Math.Max(UnsupportedStateVersion, version);
                }
                DiagnosticsLog.Info($"Skipped unsupported future state schema v{version}: {Path.GetFileName(path)}");
                return null;
            }
            return JsonSerializer.Deserialize<DeckState>(json, _json);
        }
        catch (UnsupportedStateVersionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastTryLoadIoFailure = ex is IOException or UnauthorizedAccessException;
            DiagnosticsLog.Error($"Failed to load state from {path}", ex);
            return null;
        }
    }

    private static int ReadSchemaVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Version", out var v) && v.TryGetInt32(out var version)) return Math.Max(0, version);
            if (doc.RootElement.TryGetProperty("version", out v) && v.TryGetInt32(out version)) return Math.Max(0, version);
        }
        catch { }
        return 0;
    }

    private void ArchiveUnsupportedState(string path, int version)
    {
        try
        {
            var dir = Path.Combine(_dir, "compatibility");
            Directory.CreateDirectory(dir);
            var name = $"schema-v{version}-{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(path)}";
            File.Copy(path, Path.Combine(dir, name), overwrite: true);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("Failed to archive unsupported future configuration", ex);
        }
    }

    private static void Normalize(DeckState state)
    {
        var loadedVersion = state.Version;
        state.Settings ??= new DeckSettings();
        state.Settings.CalendarLeft = double.IsFinite(state.Settings.CalendarLeft) ? Math.Clamp(state.Settings.CalendarLeft, -20000, 20000) : 120;
        state.Settings.CalendarTop = double.IsFinite(state.Settings.CalendarTop) ? Math.Clamp(state.Settings.CalendarTop, -20000, 20000) : 110;
        state.Settings.CalendarOpacity = double.IsFinite(state.Settings.CalendarOpacity) ? Math.Clamp(state.Settings.CalendarOpacity, 0.35, 1.0) : 0.86;
        state.Settings.CalendarWidth = double.IsFinite(state.Settings.CalendarWidth) ? Math.Max(state.Settings.CalendarWidth, 390) : 650;
        state.Settings.CalendarHeight = double.IsFinite(state.Settings.CalendarHeight) ? Math.Clamp(state.Settings.CalendarHeight, 310, 1200) : 510;
        state.Settings.CalendarViewMode = state.Settings.CalendarViewMode == "Month" ? "Month" : "Rolling";
        state.Settings.CalendarVisibleWeekRows = Math.Clamp(state.Settings.CalendarVisibleWeekRows, 3, 8);
        if (string.IsNullOrWhiteSpace(state.Settings.CalendarBackgroundColor)
            || !System.Text.RegularExpressions.Regex.IsMatch(state.Settings.CalendarBackgroundColor, "^#[0-9A-Fa-f]{6}$"))
            state.Settings.CalendarBackgroundColor = "#181C25";
        state.Settings.CalendarIdleOpacity = double.IsFinite(state.Settings.CalendarIdleOpacity)
            ? Math.Clamp(state.Settings.CalendarIdleOpacity, 0.15, 1.0) : 0.38;
        state.Settings.CalendarActiveBackgroundOpacity = double.IsFinite(state.Settings.CalendarActiveBackgroundOpacity)
            ? Math.Clamp(state.Settings.CalendarActiveBackgroundOpacity, 0.15, 1.0) : 0.86;
        state.Settings.GoogleCalendarClientId ??= "";
        state.Settings.StandaloneIconIdleOpacity = double.IsFinite(state.Settings.StandaloneIconIdleOpacity)
            ? Math.Clamp(state.Settings.StandaloneIconIdleOpacity, 0.15, 1.0) : 0.38;
        state.Settings.StandaloneIconHoverOpacity = double.IsFinite(state.Settings.StandaloneIconHoverOpacity)
            ? Math.Clamp(state.Settings.StandaloneIconHoverOpacity, 0.15, 1.0) : 1.0;
        state.Fences ??= [];
        state.Snapshots ??= [];
        state.MonitorProfiles ??= [];
        state.Templates ??= [];
        state.UngroupedDesktopPaths ??= [];
        state.LooseDesktopItems ??= [];
        state.CurrentPage = Math.Max(0, state.CurrentPage);
        state.Settings.GroupSnapGap = Math.Clamp(state.Settings.GroupSnapGap, 0, 64);
        state.Settings.PersistDebounceMs = Math.Clamp(state.Settings.PersistDebounceMs, 100, 2000);
        state.Settings.AutomaticSnapshotRetentionDays = Math.Clamp(state.Settings.AutomaticSnapshotRetentionDays, 1, 30);
        state.Settings.AsyncItemLoadThreshold = Math.Clamp(state.Settings.AsyncItemLoadThreshold, 20, 1000);
        state.Settings.IconCacheMaxEntries = Math.Clamp(state.Settings.IconCacheMaxEntries, 128, 4096);
        state.Settings.IconMovementAnimationDurationMs = Math.Clamp(state.Settings.IconMovementAnimationDurationMs, IconMovementAnimationOptions.MinimumDurationMs, IconMovementAnimationOptions.MaximumDurationMs);
        state.Settings.IconMovementAnimationMaxItems = Math.Clamp(state.Settings.IconMovementAnimationMaxItems, IconMovementAnimationOptions.MinimumBatchSize, IconMovementAnimationOptions.MaximumBatchSize);
        state.Settings.LargeBatchOperationThreshold = Math.Clamp(state.Settings.LargeBatchOperationThreshold, 5, 500);
        state.Settings.PortalHistoryLimit = Math.Clamp(state.Settings.PortalHistoryLimit, 5, 200);
        state.Settings.PeekHotCornerHitThicknessPx = Math.Clamp(state.Settings.PeekHotCornerHitThicknessPx, 4, 32);
        state.Settings.PeekTaskbarCustomIconPath ??= "";
        state.Settings.CrashLoopThreshold = Math.Clamp(state.Settings.CrashLoopThreshold, 2, 10);
        state.Settings.CrashLoopWindowMinutes = Math.Clamp(state.Settings.CrashLoopWindowMinutes, 2, 120);
        state.Settings.UpdateManifestSource ??= "";
        state.Settings.UpdatePublicKeyPath ??= "";

        // v7.44 migration: the v7.33-v7.43 checkbox meant "leave newly created items
        // loose on the desktop". Preserve that behavior as the explicit Desktop target,
        // while the previous unchecked state remains legacy automatic classification.
        if (loadedVersion < 28)
        {
            state.Settings.UseDefaultDesktopPlacement = state.Settings.LeaveNewDesktopItemsUngrouped;
            state.Settings.DefaultDesktopPlacementKind = CyanDeck.Automation.DefaultDesktopPlacementKind.Desktop;
            state.Settings.DefaultDesktopPlacementGroupId = null;
            state.Settings.DefaultDesktopPlacementTabId = null;
            state.Settings.ActivateDefaultDestinationTab = false;
        }

        // v7.46 runtime integration of the preserved v7.17 Details-column sync is intentionally
        // opt-in. Existing users of v6.7 Explorer view sync must not acquire column/layout sync
        // merely by upgrading.
        if (loadedVersion < 29)
        {
            foreach (var tab in state.Fences.SelectMany(f => f.Tabs ?? []))
            {
                tab.SyncExplorerDetailsColumns = false;
                tab.SyncExplorerDetailsColumnWidths = false;
            }
            foreach (var tab in state.Templates.SelectMany(t => t.Prototype?.Tabs ?? []))
            {
                tab.SyncExplorerDetailsColumns = false;
                tab.SyncExplorerDetailsColumnWidths = false;
            }
        }

        // v7.47 runtime integration of the preserved v7.18 FLIP coordinator is opt-in.
        // Explicitly seed the normalized defaults for older schemas so existing installs do not
        // start animating merely because they were upgraded.
        if (loadedVersion < 30)
        {
            state.Settings.AnimateIconsWhenMoved = false;
            state.Settings.IconMovementAnimationDurationMs = IconMovementAnimationOptions.Default.DurationMs;
            state.Settings.IconMovementAnimationMaxItems = IconMovementAnimationOptions.Default.MaxAnimatedItems;
        }

        // v7.48 is the first canonical host wiring for blank-desktop rectangle creation.
        // Older full-source builds had no rectangle-creation host at all, so there is no legacy
        // button behavior to preserve. Seed the new feature conservatively to right-button only;
        // users can opt into left-button or either-button creation in Settings.
        if (loadedVersion < 31)
        {
            state.Settings.CreateGroupMouseButtonMode = DeckCreationMouseButtonMode.RightOnly;
        }

        // v7.49 runtime-wires the preserved v7.29 linked-spacing policy. Existing
        // users keep their per-group spacing behavior until they opt in.
        if (loadedVersion < 32)
        {
            state.Settings.UseSameSpacingInsideAndOutsideGroups = false;
        }

        // v7.49.2: match Fences' Peek taskbar behavior again. The Show Fences button is
        // optional, not a permanently-present runtime surface. v7.49.1 schema-33 builds
        // force-enabled it for upgraded users; undo that one-version migration when the
        // standard icon is still in use. A configured custom icon is treated as explicit
        // opt-in and is preserved.
        if (loadedVersion == 33
            && state.Settings.PeekTaskbarButtonEnabled
            && string.IsNullOrWhiteSpace(state.Settings.PeekTaskbarCustomIconPath))
        {
            state.Settings.PeekTaskbarButtonEnabled = false;
        }

        // v7.34: migrate the v7.33 path-only loose desktop markers into stable item references.
        // Keep the legacy path list mirrored for one schema generation so imported v7.33 backups
        // and diagnostics remain understandable without creating a second source of truth.
        var looseByPath = state.LooseDesktopItems
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Path))
            .GroupBy(i => DesktopDirectoryService.Normalize(i.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in state.UngroupedDesktopPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(DesktopDirectoryService.Normalize))
        {
            if (!looseByPath.ContainsKey(path))
            {
                var item = new DeckItem { Path = path };
                state.LooseDesktopItems.Add(item);
                looseByPath[path] = item;
            }
        }
        state.LooseDesktopItems = looseByPath.Values.OrderBy(i => i.AddedUtc).ToList();
        state.UngroupedDesktopPaths = state.LooseDesktopItems.Select(i => DesktopDirectoryService.Normalize(i.Path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var item in state.LooseDesktopItems)
        {
            item.OpenCount = Math.Max(0, item.OpenCount);
            if (item.PositionX is double px && !double.IsFinite(px)) item.PositionX = null;
            if (item.PositionY is double py && !double.IsFinite(py)) item.PositionY = null;
        }

        state.Version = StateSchema.Current;
        foreach (var fence in state.Fences)
        {
            fence.PageIndex = Math.Max(0, fence.PageIndex);
            fence.IconSize = Math.Clamp(fence.IconSize, 24, 128);
            fence.IconSpacingX = Math.Clamp(fence.IconSpacingX, 0, 80);
            fence.IconSpacingY = Math.Clamp(fence.IconSpacingY, 0, 80);
            fence.RollupHoverDelayMs = Math.Clamp(fence.RollupHoverDelayMs, 100, 2000);
            fence.TitleFontSize = Math.Clamp(fence.TitleFontSize, 9, 24);
            if (string.IsNullOrWhiteSpace(fence.TitleFontFamily)) fence.TitleFontFamily = "Segoe UI";
            if (string.IsNullOrWhiteSpace(fence.TitleForeground)) fence.TitleForeground = "#F4F6FA";
            fence.Tabs ??= [];
            foreach (var tab in fence.Tabs)
            {
                if (tab.Kind == DeckTabKind.FolderPortal)
                {
                    tab.PortalRootPath ??= tab.PortalPath;
                    if (string.IsNullOrWhiteSpace(tab.PortalPath)) tab.PortalPath = tab.PortalRootPath;
                }
                tab.Items ??= [];
                foreach (var item in tab.Items) item.OpenCount = Math.Max(0, item.OpenCount);
                tab.Rules ??= [];
                tab.DetailsColumns = DeckDetailsColumnState.Normalize(tab.DetailsColumns);
                tab.PortalViewStates ??= new Dictionary<string, DeckPortalViewState>(StringComparer.OrdinalIgnoreCase);
                foreach (var viewState in tab.PortalViewStates.Values)
                    viewState.DetailsColumns = DeckDetailsColumnState.Normalize(viewState.DetailsColumns);
                tab.RulePriority = Math.Clamp(tab.RulePriority, -1000, 1000);
            }
        }
        foreach (var snapshot in state.Snapshots)
        {
            snapshot.Monitors ??= [];
            snapshot.Fences ??= [];
            snapshot.Items ??= [];
        }
        foreach (var template in state.Templates)
        {
            template.Name = string.IsNullOrWhiteSpace(template.Name) ? "Template" : template.Name.Trim();
            template.Prototype ??= new DeckFence();
            template.Prototype.Tabs ??= [];
            foreach (var tab in template.Prototype.Tabs)
            {
                tab.Items ??= [];
                foreach (var item in tab.Items) item.OpenCount = Math.Max(0, item.OpenCount);
                tab.Rules ??= [];
                tab.DetailsColumns = DeckDetailsColumnState.Normalize(tab.DetailsColumns);
                tab.PortalViewStates ??= new Dictionary<string, DeckPortalViewState>(StringComparer.OrdinalIgnoreCase);
                foreach (var viewState in tab.PortalViewStates.Values)
                    viewState.DetailsColumns = DeckDetailsColumnState.Normalize(viewState.DetailsColumns);
                if (tab.Kind == DeckTabKind.FolderPortal)
                {
                    tab.PortalRootPath ??= tab.PortalPath;
                    if (string.IsNullOrWhiteSpace(tab.PortalPath)) tab.PortalPath = tab.PortalRootPath;
                }
            }
        }
        foreach (var profile in state.MonitorProfiles)
        {
            profile.Fences ??= [];
            profile.Monitors ??= [];
        }
        var integrity = StateIntegrityService.Repair(state);
        if (!integrity.Healthy) DiagnosticsLog.Info($"State integrity normalization: {integrity.Summary}");

        // Validate persisted default placement IDs only after integrity repair has guaranteed
        // stable non-empty group/tab IDs. Folder Portals are deliberately excluded.
        var eligiblePlacementTargets = state.Fences
            .SelectMany(f => f.Tabs.Where(t => t.Kind == DeckTabKind.Collection)
                .Select(t => CyanDeck.Automation.DefaultDesktopPlacementPlanner.BuildTargetKey(
                    new CyanDeck.Automation.CollectionTabTarget(f.Id.ToString("D"), t.Id.ToString("D")))))
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.Ordinal);
        if (state.Settings.UseDefaultDesktopPlacement)
        {
            var normalizedPlacement = CyanDeck.Automation.DefaultDesktopPlacementSettingsAdapter.Normalize(
                state.Settings.DefaultDesktopPlacementKind,
                state.Settings.DefaultDesktopPlacementGroupId,
                state.Settings.DefaultDesktopPlacementTabId,
                state.Settings.ActivateDefaultDestinationTab,
                eligiblePlacementTargets);
            state.Settings.DefaultDesktopPlacementKind = normalizedPlacement.Target.Kind;
            state.Settings.DefaultDesktopPlacementGroupId = normalizedPlacement.Target.GroupId;
            state.Settings.DefaultDesktopPlacementTabId = normalizedPlacement.Target.TabId;
            state.Settings.ActivateDefaultDestinationTab = normalizedPlacement.ActivateDestinationTab;
            state.Settings.LeaveNewDesktopItemsUngrouped = normalizedPlacement.Target.Kind == CyanDeck.Automation.DefaultDesktopPlacementKind.Desktop;
        }
        else
        {
            state.Settings.DefaultDesktopPlacementGroupId = null;
            state.Settings.DefaultDesktopPlacementTabId = null;
            state.Settings.ActivateDefaultDestinationTab = false;
            state.Settings.LeaveNewDesktopItemsUngrouped = false;
        }
    }

    public DeckState Import(string path)
    {
        lock (_gate)
        {
            var state = TryLoad(path, throwOnUnsupported: true) ?? throw new InvalidDataException("The selected CyanDeck backup could not be read.");
            Normalize(state);
            return state;
        }
    }

    public void Export(DeckState state, string path)
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            // v8.0.44: never write over an existing backup (USB/cloud folder) directly. Write a
            // temporary file next to it, read it back, then swap it in.
            var payload = JsonSerializer.Serialize(state, _json);
            var temp = path + ".cyandeck-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(payload);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                VerifyExportedBackup(temp, state);
                if (File.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("백업 파일 위치가 연결 파일이라 덮어쓰지 않았습니다. 다른 위치를 선택하세요.");
                    try
                    {
                        File.Replace(temp, path, null, ignoreMetadataErrors: true);
                    }
                    catch (IOException ex) when (!IsTemporaryFileLock(ex) && File.Exists(temp))
                    {
                        // Some USB/network/cloud file systems cannot replace atomically. The new
                        // copy has been verified, so overwriting is now the safe fallback.
                        File.Copy(temp, path, overwrite: true);
                    }
                }
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // Reads an exported backup back and checks it is complete before it replaces anything.
    private void VerifyExportedBackup(string path, DeckState expected)
    {
        var json = File.ReadAllText(path);
        var version = ReadSchemaVersion(json);
        var check = JsonSerializer.Deserialize<DeckState>(json, _json)
            ?? throw new InvalidDataException("내보낸 백업을 다시 읽지 못했습니다.");
        if (version != expected.Version || (check.Fences?.Count ?? -1) != expected.Fences.Count ||
            (check.Snapshots?.Count ?? -1) != expected.Snapshots.Count)
            throw new InvalidDataException("내보낸 백업 내용이 현재 레이아웃과 일치하지 않아 저장하지 않았습니다.");
    }

    // Win32 ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33).
    // Neither cmd.exe nor PowerShell can bypass another process's sharing rules.
    private static bool IsTemporaryFileLock(IOException ex)
        => (ex.HResult & 0xFFFF) is 32 or 33;

    public void Save(DeckState state)
    {
        lock (_gate)
        {
            var sequence = Interlocked.Increment(ref _saveSequence);
            CommitLayoutPayload(() => JsonSerializer.Serialize(state, _json));
            _committedSequence = Math.Max(_committedSequence, sequence);
        }
    }

    /// <summary>
    /// v8.0.44 auto-save, step 1 (UI thread): serialize the live state. Only this step touches
    /// the in-memory model; the file work happens in <see cref="SavePrepared"/>.
    /// </summary>
    public PreparedLayoutSave PrepareSave(DeckState state)
        => new(JsonSerializer.Serialize(state, _json), Interlocked.Increment(ref _saveSequence));

    /// <summary>
    /// Step 2 (background thread): write, flush, replace and retry. Returns false when a newer
    /// payload was already committed (for example by a forced save), so nothing was written.
    /// </summary>
    public bool SavePrepared(PreparedLayoutSave prepared)
    {
        lock (_gate)
        {
            if (prepared.Sequence <= _committedSequence) return false;
            CommitLayoutPayload(() => prepared.Payload);
            _committedSequence = prepared.Sequence;
            return true;
        }
    }

    private void CommitLayoutPayload(Func<string> createPayload)
    {
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var tmp = _path + ".tmp";
                var payload = createPayload();
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(payload);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // Keep synchronous retries short; AppController handles delayed retries
                // without discarding the unsaved in-memory state.
                const int maxAttempts = 3;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        if (File.Exists(_path))
                        {
                            // Atomic replacement retains the previous layout in .bak.
                            // Never use File.Move(overwrite: true) on an existing layout.
                            File.Replace(tmp, _path, _backupPath, ignoreMetadataErrors: true);
                        }
                        else
                        {
                            // File.Move without an overwrite flag protects a destination
                            // that another process creates while this save is pending.
                            File.Move(tmp, _path);
                        }

                        if (attempt > 1)
                            DiagnosticsLog.Info($"Layout state saved after retry (attempt {attempt}).");
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // If the atomic replacement moved the staged file before an
                        // error, do not retry with an absent or different payload.
                        if (!File.Exists(tmp)) throw;
                        if (attempt < maxAttempts)
                        {
                            System.Threading.Thread.Sleep(60 * attempt);
                            continue;
                        }

                        // Existing layouts: hidden CMD launches an atomic File.Replace
                        // attempt with a .bak backup, NOT 'move /Y' or 'copy /Y'.
                        // This cannot force access to a locked or protected file.
                        var existingLayout = File.Exists(_path);
                        var cmdSucceeded = existingLayout
                            ? TryCommitExistingLayoutViaCmd(tmp, _path, _backupPath)
                            : TryCommitInitialLayoutViaCmd(tmp, _path);
                        if (cmdSucceeded)
                        {
                            DiagnosticsLog.Info(existingLayout
                                ? "Existing layout committed via hidden CMD atomic replace after bounded retries (backup retained)."
                                : "Initial layout committed via hidden CMD fallback after bounded retries.");
                            return;
                        }
                        DiagnosticsLog.Warn($"Hidden CMD layout fallback failed for {(existingLayout ? "existing" : "initial")} file (HRESULT 0x{ex.HResult:X8}; original/backup/staged layout retained where possible).");
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"Failed to save layout state: {Path.GetFileName(_path)} (HRESULT 0x{ex.HResult:X8}; staged layout retained when possible)", ex);
                throw;
            }
        }
    }

    // CMD's own move/copy overwrite commands cannot satisfy the existing
    // File.Replace + backup contract. For an existing layout, CMD launches the
    // Windows built-in PowerShell host to try the atomic .NET File.Replace API
    // in a second process instead. Both use the same Windows sharing rules.
    // Paths are encoded in the -EncodedCommand payload, never parsed by cmd.exe.
    private static bool TryCommitExistingLayoutViaCmd(string stagedPath, string destinationPath, string backupPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(stagedPath) || !File.Exists(destinationPath))
            return false;
        // File.Replace consumes its source on success and may move it on rare
        // failures. Use a separate candidate so layout.json.tmp stays available
        // for crash recovery whenever the fallback does not finish.
        var candidatePath = stagedPath + ".cmd-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(stagedPath, candidatePath);
            using (var candidate = new FileStream(candidatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                candidate.Flush(flushToDisk: true);
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powerShell))
            {
                DiagnosticsLog.Warn("Hidden CMD existing-layout fallback skipped: Windows PowerShell unavailable.");
                return false;
            }

            static string PowerShellString(string path) => "'" + path.Replace("'", "''") + "'";
            var script = "$ErrorActionPreference = 'Stop'; try { " +
                "[System.IO.File]::Replace(" + PowerShellString(candidatePath) + ", " +
                PowerShellString(destinationPath) + ", " + PowerShellString(backupPath) +
                ", $true); exit 0 } catch { exit 1 }";
            var encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var cmd = Path.Combine(systemDirectory, "cmd.exe");
            var start = new ProcessStartInfo(cmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/q");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add($"\"{powerShell}\" -NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedScript} >nul 2>nul");
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(3500))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                DiagnosticsLog.Warn("Hidden CMD atomic-replace fallback timed out.");
                return false;
            }
            return process.ExitCode == 0 && File.Exists(destinationPath)
                && File.Exists(backupPath) && !File.Exists(candidatePath);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticsLog.Warn($"Hidden CMD existing-layout fallback unavailable: {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}).");
            return false;
        }
        finally
        {
            // Cleanup is limited to our newly created throwaway candidate.
            // The recovery checkpoint, original layout and .bak are untouched.
            try { if (File.Exists(candidatePath)) File.Delete(candidatePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticsLog.Warn($"Failed to clean up CMD candidate: HRESULT 0x{ex.HResult:X8}.");
            }
        }
    }

    // Last-resort CMD fallback for initial creation. Never use /Y: if another
    // process created the destination, prompting is suppressed by <nul and the
    // attempt must fail rather than overwrite that new layout.
    private static bool TryCommitInitialLayoutViaCmd(string stagedPath, string destinationPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(stagedPath) || File.Exists(destinationPath))
            return false;
        // cmd.exe expands shell metacharacters even inside quoted arguments.
        const string unsafeCharacters = "&|<>^%!()\"\r\n";
        if (stagedPath.IndexOfAny(unsafeCharacters.ToCharArray()) >= 0
            || destinationPath.IndexOfAny(unsafeCharacters.ToCharArray()) >= 0)
        {
            DiagnosticsLog.Warn("Hidden CMD initial-layout fallback skipped: shell-unsafe path.");
            return false;
        }
        try
        {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var start = new ProcessStartInfo(cmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/q");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add($"if not exist \"{destinationPath}\" move /-Y \"{stagedPath}\" \"{destinationPath}\" <nul >nul 2>nul");
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0 && File.Exists(destinationPath) && !File.Exists(stagedPath);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticsLog.Warn($"Hidden CMD initial-layout fallback unavailable: {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}).");
            return false;
        }
    }

    public string? BeginSession(DeckState state)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var marker = Path.Combine(_dir, "session.active");
                var baseline = Path.Combine(_dir, "startup-baseline.json");
                string? recoveryPath = null;
                if (File.Exists(marker) && File.Exists(baseline))
                {
                    var recoveryDir = Path.Combine(_dir, "recovery");
                    Directory.CreateDirectory(recoveryDir);
                    recoveryPath = Path.Combine(recoveryDir, $"unclean-session-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Copy(baseline, recoveryPath, true);
                    PruneRecoveryFiles(recoveryDir);
                }
                File.WriteAllText(baseline, JsonSerializer.Serialize(state, _json));
                File.WriteAllText(marker, $"pid={Environment.ProcessId};started={DateTime.UtcNow:O}");
                return recoveryPath;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("Failed to initialize session recovery", ex);
                return null;
            }
        }
    }

    public void RefreshSessionBaseline(DeckState state)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(Path.Combine(_dir, "startup-baseline.json"), JsonSerializer.Serialize(state, _json));
            }
            catch (Exception ex) { DiagnosticsLog.Error("Failed to refresh session recovery baseline", ex); }
        }
    }

    public void EndSession()
    {
        lock (_gate)
        {
            try
            {
                var marker = Path.Combine(_dir, "session.active");
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch (Exception ex) { DiagnosticsLog.Error("Failed to clear session recovery marker", ex); }
        }
    }

    public string SaveRecoveryCheckpoint(DeckState state, string reason)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var recoveryDir = Path.Combine(_dir, "recovery");
            Directory.CreateDirectory(recoveryDir);
            var safeReason = string.Concat(reason.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')).Trim('-');
            if (string.IsNullOrWhiteSpace(safeReason)) safeReason = "checkpoint";
            var path = Path.Combine(recoveryDir, $"{safeReason}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(state, _json));
            PruneRecoveryFiles(recoveryDir);
            return path;
        }
    }

    // v8.0.44: retention is per checkpoint kind (RecoveryCheckpointRetentionPolicy), so frequent
    // automatic monitor checkpoints can no longer evict pre-update/pre-import/reset ones.
    private static void PruneRecoveryFiles(string recoveryDir)
    {
        try
        {
            var files = new DirectoryInfo(recoveryDir).GetFiles("*.json")
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var delete = RecoveryCheckpointRetentionPolicy.SelectForDeletion(
                files.Values.Select(f => (f.Name, f.LastWriteTimeUtc)), DateTime.UtcNow);
            foreach (var name in delete)
                if (files.TryGetValue(name, out var file)) file.Delete();
        }
        catch { }
    }

    public string RecoveryFolder => Path.Combine(_dir, "recovery");
    public string SettingsFolder => _dir;
}

public enum LayoutLoadOutcome
{
    NoLayout,
    Primary,
    StagedNewer,
    Backup,
    Staged,
    UnsupportedFuture,
    Unreadable
}

public sealed record PreparedLayoutSave(string Payload, long Sequence);
