using System;
using System.IO;
using System.Linq;

namespace CyanDeck.Services;

/// <summary>
/// Read-only startup automatic-install backoff based on the exact SHA-256
/// digest of the verified release package. The independent PowerShell updater
/// writes the marker only after an attempted install fails; manual checks and
/// explicit installs are never blocked. No layout or Google data is involved.
/// </summary>
public static class FailedUpdateRetryPolicy
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(12);

    public static string GetMarkerPath(string localDataRoot, string packageSha256)
    {
        if (string.IsNullOrWhiteSpace(localDataRoot) ||
            packageSha256 is null || packageSha256.Length != 64 ||
            !packageSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A valid local root and exact 64-digit package SHA-256 are required.");
        return Path.Combine(localDataRoot, "updates",
            "failed-" + packageSha256.ToLowerInvariant() + ".txt");
    }

    public static bool IsAutomaticRetryDeferred(string localDataRoot,
        string packageSha256, DateTime utcNow)
    {
        string path;
        try { path = GetMarkerPath(localDataRoot, packageSha256); }
        catch (ArgumentException) { return false; }
        try
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return false; // Never follow untrusted links.
            var elapsed = utcNow.ToUniversalTime() - info.LastWriteTimeUtc;
            // Ignore future-dated or stale markers. A corrected release has
            // a new digest, and the current digest becomes eligible in 12h.
            return elapsed >= TimeSpan.Zero && elapsed < RetryDelay;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The marker is a UX guard, never a reason to prevent the app
            // itself from starting or the verified installer from working.
            return false;
        }
    }
}
