namespace DesktopDeck.Services;

// Central path definitions. The active root stays legacy until the coordinated
// installer, updater, OAuth and calendar cutover is ready.
public static class DataRootPaths
{
    public static string LegacyRoaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopDeck");
    public static string CyanRoaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck");
    public static string LegacyLocal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopDeck");
    public static string CyanLocal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyanDeck");
    public static string ActiveRoaming => LegacyRoaming;
    public static string ActiveLocal => LegacyLocal;
}
