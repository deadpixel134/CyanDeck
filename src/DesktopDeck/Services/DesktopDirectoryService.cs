using System.Runtime.InteropServices;

namespace DesktopDeck.Services;

/// <summary>
/// Resolves the current Windows Desktop known folders without caching them for the
/// life of the process. This matters when OneDrive, Group Policy, or Explorer
/// redirects the Desktop while DesktopDeck is already running.
/// </summary>
public static class DesktopDirectoryService
{
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid FolderIdPublicDesktop = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");

    public static IReadOnlyList<string> GetRoots()
    {
        var roots = new List<string>(2);
        AddIfValid(roots, TryGetKnownFolder(FolderIdDesktop) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddIfValid(roots, TryGetKnownFolder(FolderIdPublicDesktop) ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsDirectChildOfAnyRoot(string path, IEnumerable<string> roots)
        => roots.Any(root => IsDirectChild(path, root));

    public static bool IsDirectChild(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(full);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return !string.IsNullOrWhiteSpace(parent)
                   && string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim(); }
    }

    private static void AddIfValid(List<string> roots, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Directory.Exists(full)) roots.Add(full);
        }
        catch { }
    }

    private static string? TryGetKnownFolder(Guid id)
    {
        IntPtr raw = IntPtr.Zero;
        try
        {
            var local = id;
            var hr = SHGetKnownFolderPath(ref local, 0, IntPtr.Zero, out raw);
            if (hr != 0 || raw == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(raw);
        }
        catch { return null; }
        finally
        {
            if (raw != IntPtr.Zero) Marshal.FreeCoTaskMem(raw);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
