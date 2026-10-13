namespace CyanDeck.Services;

// Standalone profile: old DesktopDeck directories are never read or modified.
public static class DataRootPaths
{
    public static string ActiveRoaming => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyanDeck");
    public static string ActiveLocal => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyanDeck");
}
