using System;
using System.IO;
using System.Linq;

namespace CyanDeck.Services;

/// <summary>
/// Fail-closed: existing, staged, backed up or recovery user state must never
/// be reset by a new-install wizard. Only pending first-run OAuth may resume.
/// </summary>
public static class FirstRunOnboardingPolicy
{
    public const string PendingMarker = "first-run-setup.pending";
    public static bool ShouldOffer(string root, bool safeMode = false,
        bool externalCommand = false, bool updateRecoveryPending = false)
    {
        if (safeMode || externalCommand || updateRecoveryPending) return false;
        try
        {
            if (!Directory.Exists(root)) return true;
            foreach (var name in new[] { "layout.json", "layout.json.bak", "layout.json.tmp",
                "startup-baseline.json", "session.active" })
                if (File.Exists(Path.Combine(root, name))) return false;
            if (File.Exists(Path.Combine(root, PendingMarker))) return true;
            foreach (var name in new[] { "calendar-events.json", "calendar-events.json.bak",
                "calendar-preferences.json", "calendar-preferences.json.bak",
                "calendar-google-token.dat", "calendar-google-owner.dat",
                "calendar-event-appearance.json", "calendar-event-appearance.json.bak" })
                if (File.Exists(Path.Combine(root, name))) return false;
            var recovery = Path.Combine(root, "recovery");
            if (Directory.Exists(recovery) && Directory.EnumerateFileSystemEntries(recovery).Any())
                return false;
            return true; // A logging or cache directory alone is not user state.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return false; }
    }
}
