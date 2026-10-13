using System.IO.Compression;

namespace DesktopDeck.Services;

/// <summary>
/// Validates the contents of a *signed* legacy update ZIP before it can become
/// an installable staged package. This does not activate the CyanDeck profile.
/// Both this policy and apply-update.ps1 enforce the same flat-or-one-root
/// layout so that unrelated sibling payloads cannot be silently ignored.
/// </summary>
public static class UpdatePackageLayoutPolicy
{
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.Ordinal)
    {
        "DesktopDeck.exe", "apply-update.ps1", "recover-desktop-icons.ps1",
        "install.ps1", "uninstall.ps1", "verify-release.ps1",
        "desktopdeck-google-client-id.txt", "DesktopDeck.update-public-key.pem",
        "README.md"
    };

    public static string Validate(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > 35)
            throw new InvalidDataException("Unexpected update ZIP file table size.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? payloadRoot = null;
        var declaredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootFiles = false;
        var executableCount = 0;
        long expandedBytes = 0;

        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') ||
                path.Contains(':') || path.Any(c => c < ' ' || "<>|?*".Contains(c)))
                throw new InvalidDataException("Unsafe path in update ZIP.");

            var directory = path.EndsWith('/');
            var parts = path.TrimEnd('/').Split('/');
            if (parts.Length is < 1 or > 2 ||
                parts.Any(p => p is "" or "." or ".." ||
                    p.EndsWith('.') || p.EndsWith(' ')) ||
                (directory && parts.Length != 1))
                throw new InvalidDataException("Unexpected path depth in update ZIP.");

            // Both Windows reparse-point entries and Unix symlinks are unsafe.
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            var windowsAttributes = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if (unixMode == 0xA000 || (windowsAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update ZIP includes a link.");

            if (!seen.Add(path.TrimEnd('/')))
                throw new InvalidDataException("Duplicate update ZIP entry (case-insensitive).");

            if (directory)
            {
                if (entry.Length != 0 || !IsSafeRootName(parts[0]))
                    throw new InvalidDataException("Unsafe update ZIP directory.");
                declaredDirectories.Add(parts[0]);
                continue;
            }

            var filename = parts[^1];
            if (!AllowedFiles.Contains(filename))
                throw new InvalidDataException("Update ZIP includes a non-application file.");

            if (parts.Length == 1)
                rootFiles = true;
            else
            {
                if (!IsSafeRootName(parts[0]))
                    throw new InvalidDataException("Unsafe update ZIP root directory.");
                if (payloadRoot is null) payloadRoot = parts[0];
                else if (!string.Equals(payloadRoot, parts[0], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update ZIP contains multiple payload directories.");
            }

            if (filename == "DesktopDeck.exe") executableCount++;
            if (entry.Length < 0 || entry.Length > 1_000_000_000 ||
                expandedBytes > 1_500_000_000 - entry.Length)
                throw new InvalidDataException("Update ZIP expanded size limit exceeded.");
            expandedBytes += entry.Length;
        }

        if (executableCount != 1)
            throw new InvalidDataException("The package must contain exactly one DesktopDeck.exe.");
        if (rootFiles && payloadRoot is not null)
            throw new InvalidDataException("Update ZIP mixes root files and a nested payload.");
        if (declaredDirectories.Count > 0 &&
            (payloadRoot is null || declaredDirectories.Count != 1 ||
             !declaredDirectories.Contains(payloadRoot)))
            throw new InvalidDataException("Update ZIP has an unexpected directory.");
        return payloadRoot ?? "";
    }

    private static bool IsSafeRootName(string name) =>
        name.Length is >= 1 and <= 128 &&
        name[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
