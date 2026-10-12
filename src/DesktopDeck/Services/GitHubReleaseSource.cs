using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopDeck.Services;

/// <summary>
/// GitHub Releases update transport. A private repo needs a per-user token in the
/// DESKTOPDECK_GITHUB_TOKEN environment variable; the binary never embeds one.
/// This transport is NOT a trust root: UpdateService still requires an independently
/// installed RSA public key, a verified manifest signature and a matching ZIP hash.
/// </summary>
public static class GitHubReleaseSource
{
    public const string DefaultChannel = "github-release://deadpixel134/DesktopDeck";
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
        source?.StartsWith("github-release://", StringComparison.OrdinalIgnoreCase) == true;

    private static (string Owner, string Repository, string? Asset) Parse(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "github-release", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new InvalidDataException("Invalid GitHub release channel URL.");
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !SafePart.IsMatch(uri.Host) ||
            parts.Any(p => !SafePart.IsMatch(p) || p is "." or ".."))
            throw new InvalidDataException("Invalid GitHub release repository or asset name.");
        return (uri.Host, parts[0], parts.Length == 2 ? parts[1] : null);
    }

    public static string PackageSource(string manifestSource, string packageFile)
    {
        var (owner, repository, _) = Parse(manifestSource);
        if (string.IsNullOrWhiteSpace(packageFile) || !SafePart.IsMatch(packageFile) ||
            !packageFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub update manifest must refer to a ZIP asset filename (no paths or URLs).");
        return $"github-release://{owner}/{repository}/{packageFile}";
    }

    private static async Task<ReleaseInfo> LatestAsync(string owner, string repository, CancellationToken token)
    {
        var url = new Uri($"https://api.github.com/repos/{owner}/{repository}/releases/latest");
        using var response = await SendGitHubAsync(url, "application/vnd.github+json", token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("GitHub release not accessible. For a private repository, configure DESKTOPDECK_GITHUB_TOKEN on this PC, or publish updates in a public release repository.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var assets = new List<GitHubAsset>();
        foreach (var element in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var id = element.GetProperty("id").GetInt64();
            var name = element.GetProperty("name").GetString() ?? "";
            var size = element.GetProperty("size").GetInt64();
            if (id > 0 && SafePart.IsMatch(name) && size > 0)
                assets.Add(new GitHubAsset(id, name, size));
        }
        return new ReleaseInfo(owner, repository, tag, assets);
    }

    public static async Task<string> ReadManifestAsync(string source, string runtime, CancellationToken token)
    {
        var (owner, repository, asset) = Parse(source);
        if (asset is not null) throw new InvalidDataException("A GitHub manifest source must refer to the repository, not an asset.");
        var release = await LatestAsync(owner, repository, token).ConfigureAwait(false);
        var manifestName = $"DesktopDeck-{release.Tag.TrimStart('v', 'V')}-{runtime}.update.json";
        var manifest = release.Assets.FirstOrDefault(a => string.Equals(a.Name, manifestName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Signed manifest asset missing in latest GitHub Release: {manifestName}");
        if (manifest.Size > MaxManifestBytes)
            throw new InvalidDataException("GitHub release manifest is too large.");
        using var response = await OpenAssetAsync(release, manifest.Id, token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content, token).ConfigureAwait(false);
        if (content.Length > MaxManifestBytes) throw new InvalidDataException("GitHub manifest exceeded the size limit.");
        return System.Text.Encoding.UTF8.GetString(content.ToArray());
    }

    public static async Task DownloadPackageAsync(string source, string destination, CancellationToken token)
    {
        var (owner, repository, assetName) = Parse(source);
        if (assetName is null || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub package source must reference a release ZIP asset.");
        var release = await LatestAsync(owner, repository, token).ConfigureAwait(false);
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
        request.Headers.UserAgent.ParseAdd("DesktopDeck-Updater/2.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var githubToken = Environment.GetEnvironmentVariable("DESKTOPDECK_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(githubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken.Trim());
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }
}
