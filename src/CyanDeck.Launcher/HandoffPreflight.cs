using System.IO;

namespace CyanDeck.Launcher;

// This is a read-only handoff to the authoritative legacy executable.
// The companion never owns its own profile, updater, OAuth, mutex or IPC.
public static class HandoffPreflight
{
    public const string LegacyExecutable = "DesktopDeck.exe";
    public const string CompanionExecutable = "CyanDeck.exe";
    public const string DiagnosticOption = "--handoff-check-only";
    public const string UnsupportedCutoverOption = "--activate-cyandeck";

    public static void ValidateArguments(IReadOnlyList<string> args)
    {
        if (args.Any(a => string.Equals(a, UnsupportedCutoverOption, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("CyanDeck profile activation is not supported by the companion launcher.");

        if (args.Any(a => string.Equals(a, DiagnosticOption, StringComparison.OrdinalIgnoreCase)) &&
            (args.Count != 1 || !string.Equals(args[0], DiagnosticOption, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The read-only handoff diagnostic cannot be combined with launch options.");
    }

    public static string ResolveLegacyExecutable(string launcherDirectory, string legacyLocalRoot)
    {
        if (string.IsNullOrWhiteSpace(launcherDirectory) || string.IsNullOrWhiteSpace(legacyLocalRoot))
            throw new ArgumentException("A complete installation and local profile location is required.");

        var fullDirectory = Path.GetFullPath(launcherDirectory);
        AssertNoReparseAncestors(fullDirectory);
        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException("CyanDeck companion installation directory was not found.");

        var target = Path.Combine(fullDirectory, LegacyExecutable);
        var attributes = GetExistingAttributes(target);
        if (attributes is null || (attributes.Value & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("The compatible DesktopDeck.exe was not found beside CyanDeck.exe.");
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The DesktopDeck.exe target is a symbolic link or junction.");
        if (new FileInfo(target).Length == 0)
            throw new InvalidDataException("DesktopDeck.exe is empty; the installation is incomplete.");

        AssertLegacyProfileUnfenced(legacyLocalRoot);
        return target;
    }

    public static void AssertLegacyProfileUnfenced(string legacyLocalRoot)
    {
        var fencePath = Path.GetFullPath(Path.Combine(
            legacyLocalRoot, "migration", "legacy-writer-fenced.json"));
        AssertNoReparseAncestors(fencePath);
        if (GetExistingAttributes(fencePath) is not null)
            throw new InvalidOperationException(
                "CyanDeck data migration fence is present; the old DesktopDeck executable is blocked.");
    }

    private static void AssertNoReparseAncestors(string path)
    {
        string? cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor))
        {
            var attributes = GetExistingAttributes(cursor);
            if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A CyanDeck handoff path traverses a junction or symbolic link.");
            var parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase))
                break;
            cursor = parent;
        }
    }

    private static FileAttributes? GetExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
