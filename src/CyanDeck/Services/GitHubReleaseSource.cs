using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyanDeck.Services;

/// <summary>
/// GitHub Releases update transport. A private repo needs a per-user token in the
/// CYANDECK_GITHUB_TOKEN environment variable; the binary never embeds one.
/// This transport is NOT a trust root: UpdateService still requires an independently
/// installed RSA public key, a verified manifest signature and a matching ZIP hash.
/// </summary>
public static class GitHubReleaseSource
{
    public const string DefaultChannel = "github-release://deadpixel134/CyanDeck";
    public const string BetaChannel = "github-release-beta://deadpixel134/CyanDeck";
    private const long MaxManifestBytes = 256 * 1024;
    private const long MaxPackageBytes = 300L * 1024 * 1024;
    private static readonly Regex SafePart = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private sealed record GitHubAsset(long Id, string Name, long Size);
    private sealed record ReleaseInfo(string Owner, string Repository, string Tag, List<GitHubAsset> Assets);

    public static bool IsGitHubReleaseSource(string? source) =>
        source?.StartsWith("github-release://", StringComparison.OrdinalIgnoreCase) == true
        || source?.StartsWith("github-release-beta://", StringComparison.OrdinalIgnoreCase) == true;

    private static (string Owner, string Repository, string? Asset, bool Beta) Parse(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("github-release" or "github-release-beta") ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new InvalidDataException("Invalid GitHub release channel URL.");
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !SafePart.IsMatch(uri.Host) ||
            parts.Any(p => !SafePart.IsMatch(p) || p is "." or ".."))
            throw new InvalidDataException("Invalid GitHub release repository or asset name.");
        return (uri.Host, parts[0], parts.Length == 2 ? parts[1] : null,
            uri.Scheme == "github-release-beta");
    }

    public static string PackageSource(string manifestSource, string packageFile)
    {
        var (owner, repository, _, beta) = Parse(manifestSource);
        if (string.IsNullOrWhiteSpace(packageFile) || !SafePart.IsMatch(packageFile) ||
            !packageFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub update manifest must refer to a ZIP asset filename (no paths or URLs).");
        return $"{(beta ? "github-release-beta" : "github-release")}://{owner}/{repository}/{packageFile}";
    }

    // GitHub's /releases/latest may still refer to the old DesktopDeck v7
    // release after repository rename. Never try to fetch/install a v7 package.
    internal static string RequireNativeCyanDeckVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) ||
            !tag.StartsWith('v') ||
            !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("GitHub release tag is not a CyanDeck semantic version.");
        if (version.Major < 8)
            throw new InvalidOperationException(
                "CyanDeck 8.0.0 or later has not been published as a public GitHub Release yet. " +
                "The existing DesktopDeck v7 release is not a CyanDeck update.");
        return version.ToString(3);
    }

    // Beta is opt-in. Github /releases/latest excludes prereleases, so select
    // the highest numeric CyanDeck version among all PUBLISHED release records.
    // A draft is not an update and must never be selected.
    internal static string SelectLatestBetaReleaseTag(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub beta release listing was not an array.");
        Version? bestVersion = null;
        string? bestTag = null;
        foreach (var entry in releases.EnumerateArray())
        {
            if (entry.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!entry.TryGetProperty("tag_name", out var tagElement)) continue;
            var tag = tagElement.GetString() ?? "";
            try { _ = RequireNativeCyanDeckVersion(tag); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { continue; }
            var version = Version.Parse(tag[1..]);
            if (bestVersion is null || version > bestVersion)
            {
                bestVersion = version;
                bestTag = tag;
            }
        }
        return bestTag ?? throw new FileNotFoundException("No published CyanDeck stable or beta release was found.");
    }

    private static async Task<ReleaseInfo> ReadReleaseAsync(string owner, string repository,
        Uri url, CancellationToken token)
    {
        using var response = await SendGitHubAsync(url, "application/vnd.github+json", token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("GitHub update release not accessible. Check the release is public and the repository is reachable.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            throw new InvalidDataException("Draft releases cannot be consumed by an update channel.");
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        _ = RequireNativeCyanDeckVersion(tag);
        var assets = new List<GitHubAsset>();
        foreach (var element in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var id = element.GetProperty("id").GetInt64();
            var name = element.GetProperty("name").GetString() ?? "";
            var size = element.GetProperty("size").GetInt64();
            if (id > 0 && SafePart.IsMatch(name) && size > 0) assets.Add(new GitHubAsset(id, name, size));
        }
        return new ReleaseInfo(owner, repository, tag, assets);
    }

    private static async Task<ReleaseInfo> LatestAsync(string owner, string repository,
        bool beta, CancellationToken token)
    {
        if (!beta)
            return await ReadReleaseAsync(owner, repository,
                new Uri($"https://api.github.com/repos/{owner}/{repository}/releases/latest"), token).ConfigureAwait(false);
        // The public releases endpoint returns published stable + prereleases.
        using var response = await SendGitHubAsync(
            new Uri($"https://api.github.com/repos/{owner}/{repository}/releases?per_page=100"),
            "application/vnd.github+json", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var tag = SelectLatestBetaReleaseTag(doc.RootElement);
        return await ReadReleaseAsync(owner, repository,
            new Uri($"https://api.github.com/repos/{owner}/{repository}/releases/tags/{tag}"), token).ConfigureAwait(false);
    }


    public static async Task<string> ReadManifestAsync(string source, string runtime, CancellationToken token)
    {
        var (owner, repository, asset, beta) = Parse(source);
        if (asset is not null) throw new InvalidDataException("A GitHub manifest source must refer to the repository, not an asset.");
        var release = await LatestAsync(owner, repository, beta, token).ConfigureAwait(false);
        var manifestName = $"CyanDeck-{RequireNativeCyanDeckVersion(release.Tag)}-{runtime}.update.json";
        var manifest = release.Assets.FirstOrDefault(a => string.Equals(a.Name, manifestName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Signed manifest asset missing in latest GitHub Release: {manifestName}");
        if (manifest.Size > MaxManifestBytes)
            throw new InvalidDataException("GitHub release manifest is too large.");
        using var response = await OpenAssetAsync(release, manifest.Id, token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content, token).ConfigureAwait(false);
        if (content.Length > MaxManifestBytes) throw new InvalidDataException("GitHub manifest exceeded the size limit.");
        // Decode through a BOM-aware reader: Encoding.GetString keeps a UTF-8
        // BOM as U+FEFF, and System.Text.Json rejects it in a string payload.
        content.Position = 0;
        using var reader = new StreamReader(content, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }

    public static async Task DownloadPackageAsync(string source, string destination, CancellationToken token)
    {
        var (owner, repository, assetName, beta) = Parse(source);
        if (assetName is null || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub package source must reference a release ZIP asset.");
        // Pin the package to the version named in the verified manifest.
        // A new beta release between manifest and ZIP retrieval must not silently
        // substitute a different archive.
        var m = Regex.Match(assetName, @"^CyanDeck-(?<version>\d+\.\d+\.\d+)-win-(?:x64|arm64)\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) throw new InvalidDataException("GitHub package asset name is not versioned.");
        var tag = "v" + m.Groups["version"].Value;
        var release = await ReadReleaseAsync(owner, repository,
            new Uri($"https://api.github.com/repos/{owner}/{repository}/releases/tags/{tag}"), token).ConfigureAwait(false);
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"GitHub Release ZIP not found: {assetName}");
        if (asset.Size > MaxPackageBytes) throw new InvalidDataException("GitHub release ZIP is too large.");
        using var response = await OpenAssetAsync(release, asset.Id, token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            copied += count;
            if (copied > MaxPackageBytes) throw new InvalidDataException("GitHub release ZIP exceeded the limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        if (copied != asset.Size) throw new InvalidDataException("GitHub release ZIP size did not match the release asset metadata.");
    }

    private static async Task<HttpResponseMessage> OpenAssetAsync(ReleaseInfo release, long id, CancellationToken token)
    {
        var uri = new Uri($"https://api.github.com/repos/{release.Owner}/{release.Repository}/releases/assets/{id}");
        var response = await SendGitHubAsync(uri, "application/octet-stream", token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
        {
            var redirect = response.Headers.Location;
            response.Dispose();
            if (redirect is null || redirect.Scheme != "https" ||
                !(redirect.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                  redirect.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Unexpected GitHub asset download redirect.");
            // Never forward the private GitHub token to GitHub's CDN.
            response = await Http.GetAsync(redirect, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<HttpResponseMessage> SendGitHubAsync(Uri url, string accept, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd("CyanDeck-Updater/2.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var githubToken = Environment.GetEnvironmentVariable("CYANDECK_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(githubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken.Trim());
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }
}
