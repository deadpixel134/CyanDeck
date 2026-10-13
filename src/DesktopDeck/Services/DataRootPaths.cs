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

    // Inspect each level before recursive enumeration to avoid following
    // junctions or symbolic links into unrelated user directories.
    private static bool HasOnlyRegularEntries(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (entry is DirectoryInfo directory) pending.Push(directory);
            }
        }
        return true;
    }

    // Stronger read-only verification than the presence-only UI indicator.
    // Any changed, added or removed file invalidates the staged copy.
    public static bool IsPreparedCopyIdentical()
    {
        try
        {
            if (!HasPreparedCriticalData) return false;
            var source = new DirectoryInfo(LegacyRoaming);
            var target = new DirectoryInfo(CyanRoaming);
            if (!source.Exists || !target.Exists) return false;
            if (!HasOnlyRegularEntries(source) || !HasOnlyRegularEntries(target)) return false;
            var sourceFiles = source.GetFiles("*", SearchOption.AllDirectories);
            var targetFiles = target.GetFiles("*", SearchOption.AllDirectories);
            if (sourceFiles.Length != targetFiles.Length) return false;
            foreach (var file in sourceFiles)
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                var relative = Path.GetRelativePath(source.FullName, file.FullName);
                var copy = new FileInfo(Path.Combine(target.FullName, relative));
                if (!copy.Exists || (copy.Attributes & FileAttributes.ReparsePoint) != 0 || copy.Length != file.Length)
                    return false;
                using var originalStream = file.OpenRead();
                using var copiedStream = copy.OpenRead();
                var originalHash = System.Security.Cryptography.SHA256.HashData(originalStream);
                var copiedHash = System.Security.Cryptography.SHA256.HashData(copiedStream);
                if (!originalHash.AsSpan().SequenceEqual(copiedHash)) return false;
            }
            var sourceDirs = source.GetDirectories("*", SearchOption.AllDirectories);
            var targetDirs = target.GetDirectories("*", SearchOption.AllDirectories);
            if (sourceDirs.Length != targetDirs.Length) return false;
            foreach (var dir in sourceDirs)
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                var copy = new DirectoryInfo(Path.Combine(target.FullName, Path.GetRelativePath(source.FullName, dir.FullName)));
                if (!copy.Exists || (copy.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // All persistent stores must switch together in a later, verified release.
    // Never infer activation merely because the destination directory exists.
    public static string ActiveRoaming => LegacyRoaming;
    public static string ActiveLocal => LegacyLocal;
}
