using System;
using System.Diagnostics;
using System.IO;

namespace CyanDeck.Services;

/// <summary>
/// v8.0.44: after "마지막 앱 업데이트 되돌리기" the version that was rolled back from is
/// recorded as held. Start-time preflight and background auto-install skip any release at
/// or below the held version, so the problematic build is not reinstalled on the next start.
/// A newer published release releases the hold automatically (security fixes keep flowing),
/// and an explicit manual install or "되돌리기 취소" clears it. Manual update checks are never
/// blocked. The hold is a per-installation local file, never part of layout backups.
/// </summary>
public static class UpdateRollbackHold
{
    public static string HoldPath(string localRoot) =>
        Path.Combine(localRoot, "updates", "auto-install-hold.txt");

    // Builds before this one ignore the hold file. Rolling back to such a build would let its
    // start-time preflight reinstall the held version, so the rollback also turns off
    // InstallUpdatesAutomatically (which every build honours) and leaves this marker; the next
    // hold-aware start turns the setting back on and removes the marker.
    public static readonly Version FirstHoldAwareVersion = new(8, 0, 44);

    public static string AutoInstallPausePath(string localRoot) =>
        Path.Combine(localRoot, "updates", "auto-install-paused-by-rollback.txt");

    public static bool HonorsHold(Version? target) =>
        target is not null && Normalize(target) >= FirstHoldAwareVersion;

    // Program version inside an application-owned rollback snapshot; null when unknown.
    public static Version? ReadSnapshotVersion(string? snapshotDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(snapshotDirectory)) return null;
            var exe = Path.Combine(snapshotDirectory, "CyanDeck.exe");
            if (!File.Exists(exe)) return null;
            var info = FileVersionInfo.GetVersionInfo(exe);
            if (info.FileMajorPart <= 0 && info.FileMinorPart <= 0 && info.FileBuildPart <= 0) return null;
            return new Version(info.FileMajorPart, info.FileMinorPart, Math.Max(0, info.FileBuildPart));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static bool HasAutoInstallPause(string localRoot)
    {
        try { return File.Exists(AutoInstallPausePath(localRoot)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool RecordAutoInstallPause(string localRoot)
    {
        try
        {
            var path = AutoInstallPausePath(localRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && (new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            File.WriteAllText(path, "InstallUpdatesAutomatically was turned off by an app rollback.");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool ClearAutoInstallPause(string localRoot)
    {
        try
        {
            var path = AutoInstallPausePath(localRoot);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static Version Normalize(Version version) =>
        new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));

    public static string Format(Version version)
    {
        var normalized = Normalize(version);
        return $"{normalized.Major}.{normalized.Minor}.{normalized.Build}";
    }

    // Pure decisions (unit-tested in CompleteResetHarness).
    public static bool IsAutomaticInstallHeld(Version? held, Version latest) =>
        held is not null && Normalize(latest) <= Normalize(held);

    public static bool ShouldReleaseHold(Version? held, Version latest) =>
        held is not null && Normalize(latest) > Normalize(held);

    // After a rollback the running build is older than the held one, so the only rollback
    // snapshot left (pre-rollback) contains the held version: the command now reinstalls it.
    public static bool IsRollbackUndo(Version? held, Version current) =>
        held is not null && Normalize(current) < Normalize(held);

    // A hold is stale once the held (or a newer) version is running again: either the rollback
    // never completed or the user explicitly installed it.
    public static bool IsStale(Version? held, Version current) =>
        held is not null && Normalize(current) >= Normalize(held);

    public static Version? ReadHeldVersion(string localRoot)
    {
        try
        {
            var path = HoldPath(localRoot);
            if (!File.Exists(path)) return null;
            if ((new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var text = File.ReadAllText(path).Trim();
            if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
            return Version.TryParse(text, out var parsed) ? Normalize(parsed) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool Record(string localRoot, Version version)
    {
        try
        {
            var path = HoldPath(localRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && (new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            File.WriteAllText(path, Format(version));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool Clear(string localRoot)
    {
        try
        {
            var path = HoldPath(localRoot);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Used by every AUTOMATIC install path (start-time preflight and background install).
    /// Releases the hold when a newer release than the held version is published.
    /// </summary>
    public static bool ShouldSkipAutomaticInstall(string localRoot, Version latest, out Version? held)
    {
        held = ReadHeldVersion(localRoot);
        if (held is null) return false;
        if (ShouldReleaseHold(held, latest))
        {
            Clear(localRoot);
            return false;
        }
        return IsAutomaticInstallHeld(held, latest);
    }

    /// <summary>Settings > 업데이트 status line; empty when no effective hold exists.</summary>
    public static string DescribeHold(string localRoot, Version current)
    {
        var held = ReadHeldVersion(localRoot);
        if (held is null || IsStale(held, current)) return "";
        return $"v{Format(held)} 자동 설치 보류 중";
    }

    /// <summary>Label for the Settings rollback button, following what the command will do.</summary>
    public static string DescribeRollbackCommand(string localRoot, Version current)
    {
        var held = ReadHeldVersion(localRoot);
        return IsRollbackUndo(held, current)
            ? $"되돌리기 취소 (v{Format(held!)} 다시 설치)…"
            : "마지막 앱 업데이트 되돌리기…";
    }
}
