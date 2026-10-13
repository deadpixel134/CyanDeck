namespace CyanDeck.Launcher;

// Depends only on the executable's directory and explicit arguments.
// No APPDATA, LOCALAPPDATA, OAuth, updates or profile selection occurs here.
public static class CyanDeckLaunchPolicy
{
    public const string DiagnosticOption = "--handoff-check-only";
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "--peek-toggle", "--safe-mode", "--ignore-crash-loop",
        "--recover-groups-at-cursor"
    };

    private static readonly HashSet<string> OptionsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "--managed-policy", "--wait-for-pid"
    };

    public static IReadOnlyList<string> ValidateArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var option = args[i];
            if (string.Equals(option, "--activate-cyandeck", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "CyanDeck data profile activation is not supported by this launcher.");
            if (!Switches.Contains(option) && !OptionsWithValue.Contains(option))
                throw new ArgumentException($"Unknown CyanDeck launcher option: {option}");
            if (!seen.Add(option))
                throw new ArgumentException($"Duplicate CyanDeck launcher option: {option}");
            result.Add(option);
            if (!OptionsWithValue.Contains(option)) continue;
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) ||
                args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for CyanDeck launcher option: {option}");
            var value = args[++i];
            if (string.Equals(option, "--wait-for-pid", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(value, out var pid) || pid <= 0))
                throw new ArgumentException("The --wait-for-pid value must be a positive process ID.");
            result.Add(value);
        }
        return result;
    }

    public static string ResolveLegacyExecutable(string launcherDirectory, string? isolatedLegacyLocalRoot = null)
    {
        if (string.IsNullOrWhiteSpace(launcherDirectory))
            throw new ArgumentException("Missing CyanDeck launcher directory.", nameof(launcherDirectory));
        var directory = Path.GetFullPath(launcherDirectory);
        var target = Path.Combine(directory, "DesktopDeck.exe");
        // Refuse a redirected install directory or linked companion executable.
        var cursor = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("CyanDeck launcher directory contains a link.");
            var parent = Path.GetDirectoryName(cursor);
            if (parent is null || parent == cursor) break;
            cursor = parent;
        }
        if (!File.Exists(target))
            throw new FileNotFoundException(
                "DesktopDeck.exe must be installed beside the compatibility launcher.", target);
        var attributes = File.GetAttributes(target);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The compatible DesktopDeck executable must be an ordinary file.");
        if (new FileInfo(target).Length == 0)
            throw new InvalidDataException("The compatible DesktopDeck executable is empty.");

        // A future profile cutover must stop the companion from launching an
        // incompatible legacy EXE, including when the fence JSON is malformed.
        var legacyLocalRoot = isolatedLegacyLocalRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopDeck");
        AssertLegacyWriterFenceAbsent(legacyLocalRoot);
        return target;
    }

    public static void AssertLegacyWriterFenceAbsent(string legacyLocalRoot)
    {
        var fence = Path.GetFullPath(Path.Combine(legacyLocalRoot, "migration", "legacy-writer-fenced.json"));
        string? cursor = fence;
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            FileAttributes? attributes;
            try { attributes = File.GetAttributes(cursor); }
            catch (FileNotFoundException) { attributes = null; }
            catch (DirectoryNotFoundException) { attributes = null; }

            if (attributes is not null)
            {
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("CyanDeck launcher cutover fence path contains a link.");
                if (string.Equals(cursor, fence, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "CyanDeck profile cutover fence blocks legacy executable handoff.");
            }

            var parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(cursor, parent, StringComparison.OrdinalIgnoreCase))
                break;
            cursor = parent;
        }
    }
}
