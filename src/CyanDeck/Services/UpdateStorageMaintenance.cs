using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CyanDeck.Services;

public sealed record UpdateStorageUsage(long RollbackBytes, int RollbackSnapshots, long StagedBytes, int StagedPackages, long BootstrapBytes)
{
    public long TotalBytes => RollbackBytes + StagedBytes + BootstrapBytes;

    public string ToDisplayString()
    {
        var megabytes = TotalBytes / (1024d * 1024d);
        return $"업데이트 백업 용량 {megabytes:0.#} MB (이전 버전 백업 {RollbackSnapshots}개, 다운로드 파일 {StagedPackages}개)";
    }
}

public sealed record UpdateStoragePruneResult(int DeletedRollbackSnapshots, int DeletedPackages, int DeletedScripts, long FreedBytes, string? SkipReason)
{
    public bool Skipped => SkipReason is not null;
}

/// <summary>
/// v8.0.43: every update copies the previous executable into updates/rollback and leaves the
/// downloaded ZIP in updates/staged, but only the snapshot named by last-rollback.txt can be
/// used. Keep that snapshot plus the newest other one, delete packages that are already
/// installed (version at or below the running one) and installer scripts older than a day.
/// Nothing is pruned while an update recovery journal (pending-*.json) exists, and only
/// CyanDeck-owned, non-link entries with the expected names are ever removed.
/// </summary>
public static class UpdateStorageMaintenance
{
    public const int ExtraRollbackSnapshotsToKeep = 1;
    public static readonly TimeSpan ScriptMaxAge = TimeSpan.FromDays(1);

    private static readonly Regex RollbackDirectoryName =
        new(@"^(before-update|pre-rollback)-[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex StagedPackageName =
        new(@"^CyanDeck-(\d+(?:\.\d+){1,3})-win-(x64|arm64)\.zip$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BootstrapScriptName =
        new(@"^apply-update-[0-9a-fA-F]{32}\.ps1$", RegexOptions.CultureInvariant);

    // Same application-owned leaf names as apply-update.ps1 $managedNames.
    private static readonly HashSet<string> ManagedSnapshotFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "CyanDeck.exe", "apply-update.ps1", "recover-desktop-icons.ps1", "install.ps1", "uninstall.ps1",
        "verify-release.ps1", "cyandeck-google-client-id.txt", "CyanDeck.update-public-key.pem"
    };

    public static UpdateStorageUsage Measure(string localRoot)
    {
        var updates = Path.Combine(localRoot, "updates");
        long rollbackBytes = 0, stagedBytes = 0, bootstrapBytes = 0;
        int rollbackCount = 0, stagedCount = 0;
        try
        {
            var rollbackRoot = Path.Combine(updates, "rollback");
            if (IsOrdinaryDirectory(rollbackRoot))
            {
                foreach (var dir in new DirectoryInfo(rollbackRoot).GetDirectories())
                {
                    if (!RollbackDirectoryName.IsMatch(dir.Name) || IsLink(dir)) continue;
                    rollbackCount++;
                    rollbackBytes += SafeSize(dir);
                }
            }
            var stagedRoot = Path.Combine(updates, "staged");
            if (IsOrdinaryDirectory(stagedRoot))
            {
                foreach (var file in new DirectoryInfo(stagedRoot).GetFiles())
                {
                    if (IsLink(file)) continue;
                    if (StagedPackageName.IsMatch(file.Name)) stagedCount++;
                    stagedBytes += file.Length;
                }
            }
            var bootstrapRoot = Path.Combine(updates, "bootstrap");
            if (IsOrdinaryDirectory(bootstrapRoot))
                bootstrapBytes = new DirectoryInfo(bootstrapRoot).GetFiles().Where(f => !IsLink(f)).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new UpdateStorageUsage(rollbackBytes, rollbackCount, stagedBytes, stagedCount, bootstrapBytes);
    }

    public static UpdateStoragePruneResult Prune(string localRoot, Version currentVersion, DateTime utcNow)
    {
        var updates = Path.Combine(localRoot, "updates");
        if (!IsOrdinaryDirectory(updates))
            return new UpdateStoragePruneResult(0, 0, 0, 0, "no-update-storage");
        try
        {
            // An unresolved journal means an install/rollback may still need its snapshot.
            if (Directory.EnumerateFiles(updates, "pending-*.json").Any())
                return new UpdateStoragePruneResult(0, 0, 0, 0, "pending-recovery");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new UpdateStoragePruneResult(0, 0, 0, 0, "unreadable");
        }

        long freed = 0;
        int deletedSnapshots = 0, deletedPackages = 0, deletedScripts = 0;
        try
        {
            deletedSnapshots = PruneRollbackSnapshots(updates, ref freed);
            deletedPackages = PruneStagedPackages(updates, currentVersion, utcNow, ref freed);
            deletedScripts = PruneBootstrapScripts(updates, utcNow, ref freed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Housekeeping is best effort; it must never affect startup or an update.
        }
        return new UpdateStoragePruneResult(deletedSnapshots, deletedPackages, deletedScripts, freed, null);
    }

    private static int PruneRollbackSnapshots(string updates, ref long freed)
    {
        var rollbackRoot = Path.Combine(updates, "rollback");
        if (!IsOrdinaryDirectory(rollbackRoot)) return 0;
        string? protectedPath = null;
        try
        {
            var marker = Path.Combine(updates, "last-rollback.txt");
            if (File.Exists(marker))
                protectedPath = Path.GetFullPath(File.ReadAllText(marker).Trim()).TrimEnd('\\', '/');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Without a readable rollback marker no snapshot can be proven unused.
            return 0;
        }

        var candidates = new DirectoryInfo(rollbackRoot).GetDirectories()
            .Where(d => RollbackDirectoryName.IsMatch(d.Name) && !IsLink(d))
            .OrderByDescending(d => d.CreationTimeUtc)
            .ToList();
        var keptExtra = 0;
        var deleted = 0;
        foreach (var dir in candidates)
        {
            var full = Path.GetFullPath(dir.FullName).TrimEnd('\\', '/');
            if (protectedPath is not null && string.Equals(full, protectedPath, StringComparison.OrdinalIgnoreCase))
                continue; // The rollback target is never removed.
            if (keptExtra < ExtraRollbackSnapshotsToKeep)
            {
                keptExtra++;
                continue;
            }
            // Only flat snapshots containing application-owned files are removed.
            if (dir.GetDirectories().Length > 0 ||
                dir.GetFiles().Any(f => IsLink(f) || !ManagedSnapshotFiles.Contains(f.Name)))
                continue;
            try
            {
                var size = SafeSize(dir);
                dir.Delete(recursive: true);
                freed += size;
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return deleted;
    }

    private static int PruneStagedPackages(string updates, Version currentVersion, DateTime utcNow, ref long freed)
    {
        var stagedRoot = Path.Combine(updates, "staged");
        if (!IsOrdinaryDirectory(stagedRoot)) return 0;
        var current = UpdateRollbackHold.Normalize(currentVersion);
        var deleted = 0;
        var newerPackages = new List<(FileInfo File, Version Version)>();
        foreach (var file in new DirectoryInfo(stagedRoot).GetFiles())
        {
            if (IsLink(file)) continue;
            if (file.Name.EndsWith(".zip.part", StringComparison.OrdinalIgnoreCase))
            {
                // An interrupted download; a running download is always younger than a day.
                if (utcNow - file.LastWriteTimeUtc > ScriptMaxAge && TryDelete(file, ref freed)) deleted++;
                continue;
            }
            var match = StagedPackageName.Match(file.Name);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed)) continue;
            var version = UpdateRollbackHold.Normalize(parsed);
            if (version <= current)
            {
                // Already installed (or older): the package is never used again.
                if (TryDelete(file, ref freed)) deleted++;
            }
            else newerPackages.Add((file, version));
        }
        // Keep only the newest not-yet-installed package.
        foreach (var (file, _) in newerPackages.OrderByDescending(p => p.Version).ThenByDescending(p => p.File.LastWriteTimeUtc).Skip(1))
            if (TryDelete(file, ref freed)) deleted++;
        return deleted;
    }

    private static int PruneBootstrapScripts(string updates, DateTime utcNow, ref long freed)
    {
        var bootstrapRoot = Path.Combine(updates, "bootstrap");
        if (!IsOrdinaryDirectory(bootstrapRoot)) return 0;
        var deleted = 0;
        foreach (var file in new DirectoryInfo(bootstrapRoot).GetFiles())
        {
            if (IsLink(file) || !BootstrapScriptName.IsMatch(file.Name)) continue;
            if (utcNow - file.LastWriteTimeUtc <= ScriptMaxAge) continue; // may still be running
            if (TryDelete(file, ref freed)) deleted++;
        }
        return deleted;
    }

    private static bool TryDelete(FileInfo file, ref long freed)
    {
        try
        {
            var size = file.Length;
            file.Delete();
            freed += size;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static long SafeSize(DirectoryInfo dir)
    {
        try { return dir.GetFiles().Where(f => !IsLink(f)).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsOrdinaryDirectory(string path)
    {
        try { return Directory.Exists(path) && !IsLink(new DirectoryInfo(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
