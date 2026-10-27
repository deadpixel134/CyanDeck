using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CyanDeck.Models;
using CyanDeck.Layout;
using CyanDeck.Animations;
using CyanDeck.Appearance;
using CyanDeck.ManagedDeployment;
using CyanDeck.QuickHide;

namespace CyanDeck.Services;

public static class DiagnosticBundleService
{
    public static void Create(string destinationPath, DeckState state, IconCacheStats? iconCacheStats = null, bool safeMode = false, ManagedPolicyDiagnosticSummary? managedPolicy = null, QuickHideStartupApplyStatus? quickHideStartupStatus = null, QuickHideShowReason? quickHideStartupShowReason = null, string? layoutLoadOutcome = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "CyanDeck", "diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "summary.txt"), BuildSummary(state, iconCacheStats, safeMode, managedPolicy, quickHideStartupStatus, quickHideStartupShowReason, layoutLoadOutcome), Encoding.UTF8);
            var logsDir = Path.Combine(tempRoot, "logs");
            Directory.CreateDirectory(logsDir);
            if (Directory.Exists(DiagnosticsLog.LogFolder))
            {
                foreach (var source in Directory.EnumerateFiles(DiagnosticsLog.LogFolder, "*.log", SearchOption.TopDirectoryOnly))
                {
                    try { WriteRedactedLog(source, Path.Combine(logsDir, Path.GetFileName(source))); }
                    catch { }
                }
                if (File.Exists(SettingsAuditService.AuditPath))
                {
                    try { WriteRedactedLog(SettingsAuditService.AuditPath, Path.Combine(logsDir, "settings-audit.jsonl")); }
                    catch { }
                }
            }
            // v8.0.43: the independent installer logs its failures under %LOCALAPPDATA%.
            // Same redaction and bounded tail as every other log; links are never followed.
            try
            {
                var updateLog = UpdateFailureDiagnostics.LogPath(DataRootPaths.ActiveLocal);
                if (File.Exists(updateLog) && (File.GetAttributes(updateLog) & FileAttributes.ReparsePoint) == 0)
                    WriteRedactedLog(updateLog, Path.Combine(logsDir, "update-installer.log"));
            }
            catch { }

            var parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            // Create next to the destination so Replace stays on the same volume.
            // Never delete an existing report before the new ZIP is complete.
            var tempZip = destinationPath + ".cyandeck-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ZipFile.CreateFromDirectory(tempRoot, tempZip, CompressionLevel.Optimal, false);
                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    if (!archive.Entries.Any(e => e.FullName == "summary.txt"))
                        throw new InvalidDataException("Diagnostic ZIP has no summary.");
                }
                if (File.Exists(destinationPath))
                {
                    if ((File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Diagnostic ZIP destination may not be a symbolic link.");
                    File.Replace(tempZip, destinationPath, null);
                }
                else File.Move(tempZip, destinationPath);
            }
            finally { try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { } }
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void WriteRedactedLog(string source, string destination)
    {
        // Diagnostic archives may be shared with other people.  Bound reads of
        // audit JSONL as well as application logs; audit files are not rotated.
        const int maxLogCharacters = 1_000_000;
        const int maxReadBytes = 3_000_000;
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var trimmed = file.Length > maxReadBytes;
        if (trimmed) file.Seek(-maxReadBytes, SeekOrigin.End);
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var body = reader.ReadToEnd();
        if (body.Length > maxLogCharacters)
        {
            body = body[^maxLogCharacters..];
            trimmed = true;
        }
        if (trimmed) body = "[older log contents omitted]\n" + body;
        body = RedactDiagnosticText(body);
        File.WriteAllText(destination, body, Encoding.UTF8);
    }

    // Keep this redaction inside the export boundary: the local log must still
    // contain enough diagnostic information to identify the failure source.
    // Never include layout JSON, OAuth caches, shortcut targets or secret files.
    private static string RedactDiagnosticText(string body)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            body = Regex.Replace(body, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);

        // Also mask user profiles from previous Windows installations or other
        // Windows accounts mentioned in stack traces / paths.
        body = Regex.Replace(body, @"(?i)\b[A-Z]:\\Users\\[^\\/\r\n""<>|]+", "%USERPROFILE%");

        // Header values. Do not use \s here: matching across line boundaries
        // could accidentally swallow unrelated diagnostic records.
        body = Regex.Replace(body, @"(?i)\b(Bearer[ \t]+)[A-Za-z0-9._~+/-]+=*", "$1[REDACTED]");
        body = Regex.Replace(body, @"(?i)\b((?:Authorization|X-Api-Key|Proxy-Authorization)[ \t]*:[ \t]*)(?!\[REDACTED\])[^\r\n]+", "$1[REDACTED]");

        // Key/value pairs: support JSON fields, property dumps and query-style
        // OAuth errors. Preserve the key name for useful issue diagnosis.
        body = Regex.Replace(body,
            @"(?i)([""']?(?:access_token|refresh_token|id_token|client_secret|api_key|password|authorization|client_assertion)[""']?[ \t]*[:=][ \t]*)(""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;&}]+)",
            m => m.Groups[1].Value + (m.Groups[2].Value.StartsWith('"') ? "\"[REDACTED]\"" : m.Groups[2].Value.StartsWith('\'') ? "'[REDACTED]'" : "[REDACTED]"));
        body = Regex.Replace(body, @"(?i)([?&](?:access_token|refresh_token|id_token|api_key|client_secret|code|key)=)[^&#\s]+", "$1[REDACTED]");

        // Common tokens can be included in exception texts without field names.
        body = Regex.Replace(body, @"\bAIza[0-9A-Za-z_-]{20,}\b", "[REDACTED:API_KEY]");
        body = Regex.Replace(body, @"\bya29\.[0-9A-Za-z._-]{12,}\b", "[REDACTED:OAUTH_TOKEN]");
        body = Regex.Replace(body, @"\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", "[REDACTED:JWT]");
        body = Regex.Replace(body, @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[REDACTED:EMAIL]");
        return body;
    }

    private static string BuildSummary(DeckState state, IconCacheStats? iconCacheStats, bool safeMode, ManagedPolicyDiagnosticSummary? managedPolicy, QuickHideStartupApplyStatus? quickHideStartupStatus, QuickHideShowReason? quickHideStartupShowReason, string? layoutLoadOutcome = null)
    {
        var sb = new StringBuilder();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        sb.AppendLine("CyanDeck diagnostics summary");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        sb.AppendLine($"CyanDeck assembly: {version}");
        sb.AppendLine($"State schema: {state.Version} (supported={StateSchema.Current})");
        sb.AppendLine($"Safe mode: {safeMode}");
        if (managedPolicy is { } policy)
        {
            sb.AppendLine($"Managed policy active: {policy.Active}");
            sb.AppendLine($"Managed policy style: {policy.DeploymentStyle?.ToString() ?? "none"}");
            sb.AppendLine($"Managed policy source kind: {policy.SourceKind?.ToString() ?? "none"}");
            sb.AppendLine($"Managed policy hash prefix: {policy.HashPrefix ?? "none"}");
            sb.AppendLine($"Managed locked setting count: {policy.LockedSettingCount}");
            sb.AppendLine($"Managed capability override count: {policy.CapabilityOverrideCount}");
        }
        else
        {
            sb.AppendLine("Managed policy active: false");
        }
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Current page: {state.CurrentPage + 1}");
        var pageDistribution = state.Fences
            .GroupBy(f => Math.Max(0, f.PageIndex))
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key + 1}:{g.Count()}");
        sb.AppendLine($"Group page distribution: {string.Join(", ", pageDistribution)}");
        sb.AppendLine($"Desktop page inputs: edge={state.Settings.EnableEdgePageGestures}, interior-seam={state.Settings.AllowInteriorMonitorSeamPageNavigation}, touch-two-finger={state.Settings.EnableTouchPageGestures}, touch-icon-flick={state.Settings.EnableTouchIconFlick}, keyboard-page-commands={state.Settings.EnablePageKeyboardNavigation}, alt-wheel={state.Settings.EnablePageAltMouseWheel}, middle-drag={state.Settings.EnablePageMiddleDrag}, both-buttons-drag={state.Settings.EnablePageBothButtonsDrag}");
        sb.AppendLine($"Page switch behavior: unhide={state.Settings.UnhideWhenSwitchingPages}, animate={state.Settings.AnimatePageTransitions}, enhanced-easing={state.Settings.EnhancedDesktopPageEasing}, easing-profile={state.Settings.DesktopPageEasingProfile}, duration-ms={state.Settings.PageTransitionDurationMs}, system-animation={WpfDesktopPageTransitionAnimationFactory.AreSystemAnimationsAllowed()}, bounce={state.Settings.BouncePageTransitions}");
        sb.AppendLine($"Managed system icons: standalone-recycle={state.Settings.ShowStandaloneRecycleBin}, group-recycle={state.Settings.ShowRecycleBinInSystemGroup}, native-icons-hidden={state.Settings.HideNativeDesktopIcons}");
        sb.AppendLine($"Groups: {state.Fences.Count}");
        sb.AppendLine($"Tabs: {state.Fences.Sum(f => f.Tabs.Count)}");
        sb.AppendLine($"Snapshots: {state.Snapshots.Count}");
        sb.AppendLine($"Automatic daily snapshots: {state.Settings.EnableAutomaticDailySnapshots}, retention={state.Settings.AutomaticSnapshotRetentionDays} day(s)");
        sb.AppendLine($"Layout adjustment: {state.Settings.LayoutAdjustmentMode}");
        sb.AppendLine($"Monitor profiles: {state.MonitorProfiles.Count}");
        sb.AppendLine($"Peek respects working area: {state.Settings.PeekRespectWorkingArea}");
        sb.AppendLine($"Layout save debounce: {state.Settings.PersistDebounceMs} ms");
        sb.AppendLine($"Protect multi-selection during drag: {state.Settings.ProtectMultiSelectionDuringDrag}");
        sb.AppendLine($"Titlebar controls on mouseover: {state.Settings.ShowTitlebarControlsOnMouseover}");
        sb.AppendLine($"Inactive-tab titlebar controls: {state.Settings.ShowTitlebarControlsInInactiveTabs}");
        sb.AppendLine($"Shortcut badges: hide-file={state.Settings.HideFileShortcutBadges}, hide-internet={state.Settings.HideInternetShortcutBadges}, portal={state.Settings.ShowShortcutBadgesInFolderPortals}");
        sb.AppendLine($"Blank-desktop creation button: {state.Settings.CreateGroupMouseButtonMode}");
        sb.AppendLine($"Hide inactive scrollbars: {state.Settings.HideScrollbarsWhenInactive}");
        sb.AppendLine($"Animate item changes: {state.Settings.AnimateItemChanges}");
        sb.AppendLine($"Icon movement animation: enabled={state.Settings.AnimateIconsWhenMoved}, duration-ms={state.Settings.IconMovementAnimationDurationMs}, max-items={state.Settings.IconMovementAnimationMaxItems}, system-animation={WpfIconMovementAnimator.ReadSystemAnimationPreference()}");
        sb.AppendLine($"Linked icon spacing: enabled={state.Settings.UseSameSpacingInsideAndOutsideGroups}, desktop-h-dip={LooseDesktopIconLayoutProfile.HorizontalSpacingDip:F0}, desktop-v-dip={LooseDesktopIconLayoutProfile.VerticalSpacingDip:F0}");
        sb.AppendLine($"Large batch confirmation: {state.Settings.ConfirmLargeBatchOperations}, threshold={state.Settings.LargeBatchOperationThreshold}");
        sb.AppendLine($"Folder Portal history limit: {state.Settings.PortalHistoryLimit}");
        sb.AppendLine($"Async item loading: {state.Settings.EnableAsyncItemLoading}, threshold={state.Settings.AsyncItemLoadThreshold}");
        sb.AppendLine($"Defer hidden-page rendering: {state.Settings.DeferHiddenPageRendering}");
        sb.AppendLine($"Icon cache configured capacity: {state.Settings.IconCacheMaxEntries}");
        if (iconCacheStats is IconCacheStats stats)
            sb.AppendLine($"Icon cache live stats: count={stats.Count}, capacity={stats.Capacity}, hits={stats.Hits}, misses={stats.Misses}, evictions={stats.Evictions}");
        sb.AppendLine($"Quick-hide: enabled={state.Settings.EnableQuickHide}, mode={state.Settings.QuickHideMode}, always-show-at-startup={state.Settings.QuickHideAlwaysShowIconsAtStartup}");
        // v8.0.43: no global hotkeys or keyboard hook exist; keyboard use is via single-instance command shortcuts.
        sb.AppendLine($"Input commands: desktop-double-click={state.Settings.EnableDesktopDoubleClickQuickHide}, tray-double-click={state.Settings.TrayDoubleClickAction}, global-hotkeys=none, keyboard-hook=none, hot-corner-px={state.Settings.PeekHotCornerHitThicknessPx}");
        sb.AppendLine($"Automation: auto-import={state.Settings.AutoImportDesktop}, keep-rules-updated={state.Settings.KeepRulesUpdatedAtAllTimes}");
        sb.AppendLine($"Default desktop placement: enabled={state.Settings.UseDefaultDesktopPlacement}, kind={state.Settings.DefaultDesktopPlacementKind}, group-id-set={!string.IsNullOrWhiteSpace(state.Settings.DefaultDesktopPlacementGroupId)}, tab-id-set={!string.IsNullOrWhiteSpace(state.Settings.DefaultDesktopPlacementTabId)}, activate-tab={state.Settings.ActivateDefaultDestinationTab}");
        sb.AppendLine($"Peek taskbar button: enabled={state.Settings.PeekTaskbarButtonEnabled}, custom-icon={(!string.IsNullOrWhiteSpace(state.Settings.PeekTaskbarCustomIconPath))}");
        sb.AppendLine("Fence hosting: top-level desktop-band (non-topmost)");
        sb.AppendLine($"Quick-hide startup normalization: status={quickHideStartupStatus?.ToString() ?? "not-run"}, reason={quickHideStartupShowReason?.ToString() ?? "none"}");
        sb.AppendLine($"Managed loose desktop icons: {state.LooseDesktopItems.Count}, quick-hide excluded={state.LooseDesktopItems.Count(i => i.ExcludeFromQuickHide)}");
        sb.AppendLine($"Roll-up: enabled={state.Settings.EnableRollup}, double-click={state.Settings.EnableRollupDoubleClick}, edge-dock={state.Settings.EnableRollupEdgeDock}");
        sb.AppendLine($"Snap group size to icon multiples: {state.Settings.SnapGroupSizeToIconMultiples}");
        sb.AppendLine($"Track desktop folder redirection: {state.Settings.TrackDesktopLocationChanges}");
        var startupHealth = StartupHealthService.GetSnapshot(state.Settings.CrashLoopWindowMinutes, state.Settings.CrashLoopThreshold);
        sb.AppendLine($"Crash-loop protection: enabled={state.Settings.EnableCrashLoopProtection}, threshold={state.Settings.CrashLoopThreshold}, window={state.Settings.CrashLoopWindowMinutes} min, recent-unclean={startupHealth.RecentUncleanStarts}");
        sb.AppendLine($"Update checks: automatic={state.Settings.EnableAutomaticUpdateChecks}, source-configured={!string.IsNullOrWhiteSpace(UpdateService.ResolveManifestSource(state.Settings.UpdateManifestSource, state.Settings.EnableBetaUpdateChannel))}, require-signed=true (enforced), trusted-key-configured={!string.IsNullOrWhiteSpace(UpdateService.ResolvePublicKeyPath(state.Settings.UpdatePublicKeyPath))}, last-check={state.Settings.LastUpdateCheckUtc:O}");
        sb.AppendLine($"Update recovery: pending={UpdateService.HasPendingRecovery()} (application files only; user data excluded)");
        AppendUpdateState(sb, state);
        var loadOutcome = string.IsNullOrWhiteSpace(layoutLoadOutcome) ? "unknown" : layoutLoadOutcome;
        sb.AppendLine($"Layout loaded from: {loadOutcome}");
        sb.AppendLine($"Resolved desktop roots: {DesktopDirectoryService.GetRoots().Count}");
        sb.AppendLine();
        sb.AppendLine("Monitors:");
        foreach (var m in MonitorConfigurationService.CaptureMonitors())
        {
            sb.AppendLine($"- {m.DeviceName} primary={m.Primary} bounds={m.X},{m.Y} {m.Width}x{m.Height} work={m.WorkingX},{m.WorkingY} {m.WorkingWidth}x{m.WorkingHeight}");
        }
        sb.AppendLine();
        sb.AppendLine("Privacy: this bundle does not include CyanDeck layout JSON or desktop item paths.");
        return sb.ToString();
    }

    // v8.0.43: update channel, automatic install, recent failed packages, rollback availability
    // and hold, and update backup storage. Counts and flags only; no paths are written.
    private static void AppendUpdateState(StringBuilder sb, DeckState state)
    {
        var channel = state.Settings.EnableBetaUpdateChannel ? "beta"
            : string.IsNullOrWhiteSpace(state.Settings.UpdateManifestSource) ? "stable" : "custom";
        sb.AppendLine($"Update channel: {channel}, auto-install={state.Settings.InstallUpdatesAutomatically}, auto-check={state.Settings.EnableAutomaticUpdateChecks}");
        try
        {
            var failures = UpdateFailureDiagnostics.Inspect(DataRootPaths.ActiveLocal, DateTime.UtcNow);
            var rollbackAvailable = !string.IsNullOrWhiteSpace(UpdateService.GetLatestRollbackDirectory());
            var held = UpdateRollbackHold.ReadHeldVersion(DataRootPaths.ActiveLocal);
            var heldText = held is null ? "none" : UpdateRollbackHold.Format(held);
            var usage = UpdateStorageMaintenance.Measure(DataRootPaths.ActiveLocal);
            sb.AppendLine($"Update failures: recent-failed-packages={failures.RecentFailedPackages}, installer-log={failures.HasLog}");
            sb.AppendLine($"Update rollback: available={rollbackAvailable}, auto-install-hold={heldText}");
            sb.AppendLine($"Update storage: rollback-snapshots={usage.RollbackSnapshots}, staged-packages={usage.StagedPackages}, total-mb={usage.TotalBytes / (1024 * 1024)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sb.AppendLine("Update state: unavailable");
        }
    }
}
