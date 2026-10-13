namespace DesktopDeck.Services;

// Central path definitions. The active root stays legacy until the coordinated
// installer, updater, OAuth and calendar cutover is ready.
public static class DataRootPaths
{
    public static string LegacyRoaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopDeck");
    public static string CyanRoaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck");
    public static string LegacyLocal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopDeck");
    public static string CyanLocal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyanDeck");
    // A prepared copy is not an activated profile. Callers may inspect the
    // destination without ever redirecting the running application to it.
    public static bool HasPreparedCyanRoamingCopy =>
        Directory.Exists(CyanRoaming) &&
        File.Exists(Path.Combine(CyanRoaming, "layout.json"));

    // Read-only readiness signal; never activates a profile. A copy is only
    // eligible for a later cutover if the critical files are present and its
    // recovery candidates can be inspected by the migration verifier.
    public static bool HasPreparedCriticalData =>
        HasPreparedCyanRoamingCopy &&
        File.Exists(Path.Combine(CyanRoaming, "calendar-events.json")) ==
        File.Exists(Path.Combine(LegacyRoaming, "calendar-events.json")) &&
        File.Exists(Path.Combine(CyanRoaming, "calendar-google-token.dat")) ==
        File.Exists(Path.Combine(LegacyRoaming, "calendar-google-token.dat")) &&
        File.Exists(Path.Combine(CyanRoaming, "calendar-google-owner.dat")) ==
        File.Exists(Path.Combine(LegacyRoaming, "calendar-google-owner.dat"));

    // All persistent stores must switch together in a later, verified release.
    // Never infer activation merely because the destination directory exists.
    public static string ActiveRoaming => LegacyRoaming;
    public static string ActiveLocal => LegacyLocal;
}
