using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopDeck.Services;

public sealed class DesktopDeckUpdateManifest
{
    public string Version { get; set; } = "";
    public int Schema { get; set; }
    public string Runtime { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ReleaseNotesUrl { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime? PublishedUtc { get; set; }
    public string SignatureAlgorithm { get; set; } = "";
    public string Signature { get; set; } = "";
    public string KeyId { get; set; } = "";
}

public sealed record DesktopDeckUpdateCheck(
    DesktopDeckUpdateManifest Manifest,
    Version CurrentVersion,
    Version LatestVersion,
    string ManifestSource,
    bool UpdateAvailable,
    bool SignatureVerified,
    string SignatureKeyId);

public static class UpdateService
{
    // A HTTPS URL must not silently follow a redirect to an insecure HTTP endpoint.
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    static UpdateService()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopDeck-Updater/1.0");
    }

    public static string RuntimeId => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64"
    };

    public static string ResolveManifestSource(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Environment.ExpandEnvironmentVariables(configured.Trim());
        var fromEnvironment = Environment.GetEnvironmentVariable("DESKTOPDECK_UPDATE_MANIFEST");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return Environment.ExpandEnvironmentVariables(fromEnvironment.Trim());
        var adjacent = Path.Combine(AppContext.BaseDirectory, "DesktopDeck.update.json");
        return File.Exists(adjacent) ? adjacent : GitHubReleaseSource.DefaultChannel;
    }

    public static async Task<DesktopDeckUpdateCheck> CheckAsync(string configuredSource, bool requireSignature = true, string configuredPublicKeyPath = "", CancellationToken cancellationToken = default)
    {
        var source = ResolveManifestSource(configuredSource);
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("No update manifest source is configured. Add an HTTPS URL or a local manifest path in About & troubleshooting.");

        var payload = await ReadTextAsync(source, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<DesktopDeckUpdateManifest>(payload, Json)
            ?? throw new InvalidDataException("The update manifest could not be parsed.");
        if (!TryParseVersion(manifest.Version, out var latest))
            throw new InvalidDataException($"The update manifest has an invalid version: '{manifest.Version}'.");
        if (manifest.Schema > 0 && manifest.Schema < StateSchema.Current)
            DiagnosticsLog.Info($"Update manifest schema v{manifest.Schema} is older than the current app schema v{StateSchema.Current}.");
        if (!string.IsNullOrWhiteSpace(manifest.Runtime) && !string.Equals(manifest.Runtime, RuntimeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The update manifest is for {manifest.Runtime}, but this process is {RuntimeId}.");

        // All installation-capable update channels must be signed, regardless of legacy v35 settings.
        var signatureVerified = VerifyManifestSignature(manifest, true, configuredPublicKeyPath, out var keyId);
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        return new DesktopDeckUpdateCheck(manifest, current, latest!, source, latest! > current, signatureVerified, keyId);
    }


    public static string ResolvePublicKeyPath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
            if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(AppContext.BaseDirectory, expanded);
            return Path.GetFullPath(expanded);
        }
        var fromEnvironment = Environment.GetEnvironmentVariable("DESKTOPDECK_UPDATE_PUBLIC_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var expanded = Environment.ExpandEnvironmentVariables(fromEnvironment.Trim());
            if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(AppContext.BaseDirectory, expanded);
            return Path.GetFullPath(expanded);
        }
        var adjacent = Path.Combine(AppContext.BaseDirectory, "DesktopDeck.update-public-key.pem");
        return File.Exists(adjacent) ? adjacent : "";
    }

    public static string GetPublicKeyFingerprint(RSA rsa)
    {
        var spki = rsa.ExportSubjectPublicKeyInfo();
        return Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    }

    public static string BuildCanonicalManifestPayload(DesktopDeckUpdateManifest manifest)
    {
        static string Escape(string? value) => (value ?? "").Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
        return string.Join("\n", new[]
        {
            "DesktopDeck.UpdateManifest.v1",
            $"version={Escape(manifest.Version.Trim())}",
            $"schema={manifest.Schema}",
            $"runtime={Escape(manifest.Runtime.Trim())}",
            $"packageUrl={Escape(manifest.PackageUrl.Trim())}",
            $"sha256={Escape(NormalizeHash(manifest.Sha256))}",
            $"releaseNotesUrl={Escape(manifest.ReleaseNotesUrl.Trim())}",
            $"notes={Escape(manifest.Notes)}"
        });
    }

    private static bool VerifyManifestSignature(DesktopDeckUpdateManifest manifest, bool requireSignature, string configuredPublicKeyPath, out string keyId)
    {
        keyId = "";
        var hasSignature = !string.IsNullOrWhiteSpace(manifest.Signature);
        if (!hasSignature)
        {
            if (requireSignature) throw new InvalidDataException("This update channel requires a signed manifest, but the manifest is unsigned.");
            return false;
        }
        if (!string.Equals(manifest.SignatureAlgorithm, "RSA-PSS-SHA256", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported update signature algorithm: '{manifest.SignatureAlgorithm}'. Expected RSA-PSS-SHA256.");

        var keyPath = ResolvePublicKeyPath(configuredPublicKeyPath);
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            throw new FileNotFoundException("The update manifest is signed, but a trusted local public key was not found. Configure the public-key PEM path in About & troubleshooting.", keyPath);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(keyPath));
        keyId = GetPublicKeyFingerprint(rsa);
        if (!string.IsNullOrWhiteSpace(manifest.KeyId) && !string.Equals(NormalizeHash(manifest.KeyId), keyId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Update signing key fingerprint mismatch. Manifest keyId={manifest.KeyId}; trusted key={keyId}.");
        byte[] signature;
        try { signature = Convert.FromBase64String(manifest.Signature.Trim()); }
        catch (FormatException ex) { throw new InvalidDataException("The update manifest signature is not valid Base64.", ex); }
        var payload = Encoding.UTF8.GetBytes(BuildCanonicalManifestPayload(manifest));
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("The update manifest signature is invalid. The manifest may have been modified or signed with a different key.");
        DiagnosticsLog.Info($"Verified update manifest signature with key {keyId}.");
        return true;
    }

    public static async Task<string> StagePackageAsync(DesktopDeckUpdateCheck check, CancellationToken cancellationToken = default)
    {
        if (!check.SignatureVerified)
            throw new InvalidDataException("Unsigned or unverified update manifests may not stage an executable package.");
        if (string.IsNullOrWhiteSpace(check.Manifest.PackageUrl))
            throw new InvalidDataException("The update manifest does not contain packageUrl.");
        if (string.IsNullOrWhiteSpace(check.Manifest.Sha256))
            throw new InvalidDataException("The update manifest does not contain a SHA-256 package hash.");

        var packageSource = ResolveRelativeSource(check.ManifestSource, check.Manifest.PackageUrl);
        var updatesDir = Path.Combine(DataRootPaths.ActiveLocal, "updates", "staged");
        Directory.CreateDirectory(updatesDir);
        var destination = Path.Combine(updatesDir, $"DesktopDeck-{check.LatestVersion}-{RuntimeId}.zip");
        var temp = destination + ".part";
        if (File.Exists(temp)) File.Delete(temp);

        await CopySourceToFileAsync(packageSource, temp, cancellationToken).ConfigureAwait(false);
        var expected = NormalizeHash(check.Manifest.Sha256);
        var actual = await ComputeSha256Async(temp, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException($"Update package SHA-256 mismatch. Expected {expected}; got {actual}.");
        }

        // Reject ambiguous/mixed-root ZIPs before publishing a staged update.
        // The PowerShell installer validates the layout again after shutdown.
        using (var archive = ZipFile.OpenRead(temp))
            UpdatePackageLayoutPolicy.Validate(archive);
        File.Move(temp, destination, true);
        return destination;
    }

    public static string PendingRecoveryJournalPath()
    {
        var install = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(install))).ToLowerInvariant();
        return Path.Combine(DataRootPaths.ActiveLocal, "updates", $"pending-{digest}.json");
    }

    public static bool HasPendingRecovery() => File.Exists(PendingRecoveryJournalPath());

    public static bool TryLaunchPendingRecovery(int waitForPid, bool relaunch = true)
    {
        if (!HasPendingRecovery()) return false;
        var script = Path.Combine(AppContext.BaseDirectory, "apply-update.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("CyanDeck update recovery script is missing", script);
        var install = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var args = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(script)} -RecoverPending -InstallDir {Quote(install)} -WaitForPid {waitForPid}" + (relaunch ? " -Relaunch" : "");
        return Process.Start(new ProcessStartInfo("powershell.exe", args)
        {
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden
        }) is not null;
    }

    public static bool TryLaunchInstall(string packagePath, string expectedSha256, int waitForPid, bool relaunch = true)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("A signed manifest's 64-character SHA-256 digest is required for installation.");
        var script = Path.Combine(AppContext.BaseDirectory, "apply-update.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("apply-update.ps1 is missing from the CyanDeck installation.", script);
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var args = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(script)} -Package {Quote(packagePath)} -ExpectedSha256 {Quote(expectedSha256)} -InstallDir {Quote(installDir)} -WaitForPid {waitForPid}" + (relaunch ? " -Relaunch" : "");
        return Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }) is not null;
    }

    public static bool TryLaunchRollback(int waitForPid, bool relaunch = true)
    {
        var rollback = GetLatestRollbackDirectory();
        if (string.IsNullOrWhiteSpace(rollback) || !Directory.Exists(rollback)) return false;
        var script = Path.Combine(AppContext.BaseDirectory, "apply-update.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("apply-update.ps1 is missing from the CyanDeck installation.", script);
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var args = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(script)} -RollbackDir {Quote(rollback)} -InstallDir {Quote(installDir)} -WaitForPid {waitForPid}" + (relaunch ? " -Relaunch" : "");
        Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        return true;
    }

    public static string? GetLatestRollbackDirectory()
    {
        try
        {
            var marker = Path.Combine(DataRootPaths.ActiveLocal, "updates", "last-rollback.txt");
            if (!File.Exists(marker)) return null;
            var path = File.ReadAllText(marker).Trim();
            return Directory.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static async Task<string> ReadTextAsync(string source, CancellationToken cancellationToken)
    {
        if (GitHubReleaseSource.IsGitHubReleaseSource(source))
            return await GitHubReleaseSource.ReadManifestAsync(source, RuntimeId, cancellationToken).ConfigureAwait(false);
        if (TryHttp(source, out var uri))
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode(); // also rejects any redirect, including HTTPS -> HTTP
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        var path = ToLocalPath(source);
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopySourceToFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (GitHubReleaseSource.IsGitHubReleaseSource(source))
        {
            await GitHubReleaseSource.DownloadPackageAsync(source, destination, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (TryHttp(source, out var uri))
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        File.Copy(ToLocalPath(source), destination, true);
    }

    private static string ResolveRelativeSource(string manifestSource, string packageSource)
    {
        if (Uri.TryCreate(packageSource, UriKind.Absolute, out var packageUri))
        {
            if (packageUri.Scheme == "http") throw new InvalidDataException("HTTP update packages are forbidden.");
            if (packageUri.Scheme is "https" or "file") return packageUri.ToString();
        }
        if (GitHubReleaseSource.IsGitHubReleaseSource(manifestSource))
            return GitHubReleaseSource.PackageSource(manifestSource, packageSource);
        if (TryHttp(manifestSource, out var manifestUri)) return new Uri(manifestUri, packageSource).ToString();
        var manifestPath = ToLocalPath(manifestSource);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath) ?? AppContext.BaseDirectory, packageSource));
    }

    private static bool TryHttp(string source, out Uri uri)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var parsed))
        {
            if (parsed.Scheme == "http")
                throw new InvalidDataException("Insecure HTTP update sources are forbidden. Use HTTPS or a local signed manifest.");
            if (parsed.Scheme == "https")
            {
                uri = parsed;
                return true;
            }
        }
        uri = null!;
        return false;
    }

    private static string ToLocalPath(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile) return uri.LocalPath;
        var expanded = Environment.ExpandEnvironmentVariables(source);
        if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(AppContext.BaseDirectory, expanded);
        return Path.GetFullPath(expanded);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeHash(string value) => new(value.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryParseVersion(string text, out Version? version)
    {
        var value = (text ?? "").Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        if (!Version.TryParse(value, out var parsed))
        {
            version = null;
            return false;
        }
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
