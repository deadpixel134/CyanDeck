using System.Text;
using System.Text.Json;

namespace CyanDeck.Calendar;

/// <summary>
/// Desktop OAuth application credentials, distinct from user access/refresh tokens.
/// An installed-app client_secret is not a confidential application secret (Google
/// documents it as distributable). Never include actual credentials in source ZIPs.
/// </summary>
public static class GoogleOAuthClientConfiguration
{
    public const string BundledIdFile = "cyandeck-google-client-id.txt";
    public const string InstalledClientFile = "cyandeck-google-oauth-client.json";
    // Embedded by the DISTRIBUTOR during build; consumers never download OAuth JSON.
    public const string EmbeddedResourceName = "CyanDeck.OAuthDesktopClient";

    public static string Resolve(string? userConfiguredId, string? installationDirectory = null)
    {
        // The app-provided credentials win over obsolete per-user client-ID settings.
        // This preserves a one-click Google sign-in after the distributor provisions the build.
        var embedded = ReadEmbeddedClient();
        if (embedded is not null) return embedded.Value.ClientId;
        // Older unprovisioned builds retain their legacy manual-ID behavior.
        if (!string.IsNullOrWhiteSpace(userConfiguredId)) return userConfiguredId.Trim();

        var installed = ReadInstalledClient(installationDirectory);
        if (installed is not null) return installed.Value.ClientId;

        try
        {
            var path = Path.Combine(installationDirectory ?? AppContext.BaseDirectory, BundledIdFile);
            if (!File.Exists(path)) return "";
            var candidate = File.ReadAllText(path, Encoding.UTF8).Trim().TrimStart('\uFEFF');
            return IsValidClientId(candidate) ? candidate : "";
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>True when this build (or legacy installation) has app-level credentials.</summary>
    public static bool HasCompleteClient(string? installationDirectory = null) =>
        ReadInstalledClient(installationDirectory) is not null;

    /// <summary>Returns empty for clients that permit PKCE without a secret.</summary>
    public static string ResolveSecret(string effectiveClientId, string? installationDirectory = null)
    {
        var installed = ReadInstalledClient(installationDirectory);
        if (installed is null) return "";
        if (!string.Equals(effectiveClientId, installed.Value.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Google OAuth 클라이언트 ID가 데스크톱 인증 JSON과 다릅니다. 설정의 수동 ID를 지우거나 일치하는 데스크톱 OAuth JSON을 사용하세요.");
        return installed.Value.ClientSecret;
    }

    private static bool IsValidClientId(string candidate) =>
        candidate.Length is >= 32 and <= 300 && !candidate.Any(char.IsWhiteSpace)
        && candidate.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase)
        && candidate.Contains('-');

    private static (string ClientId, string ClientSecret)? ReadInstalledClient(string? installationDirectory)
    {
        var embedded = ReadEmbeddedClient();
        if (embedded is not null) return embedded;

        // Legacy sidecar remains readable for developer builds. A normal release
        // has its Desktop OAuth configuration INSIDE CyanDeck.exe instead.
        var path = Path.Combine(installationDirectory ?? AppContext.BaseDirectory, InstalledClientFile);
        if (!File.Exists(path)) return null;
        return ParseInstalledClient(File.ReadAllText(path, Encoding.UTF8));
    }

    private static (string ClientId, string ClientSecret)? ReadEmbeddedClient()
    {
        using var resource = typeof(GoogleOAuthClientConfiguration).Assembly
            .GetManifestResourceStream(EmbeddedResourceName);
        if (resource is null) return null;
        using var reader = new StreamReader(resource, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ParseInstalledClient(reader.ReadToEnd());
    }

    private static (string ClientId, string ClientSecret) ParseInstalledClient(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            // Native PKCE / 127.0.0.1 flow must not use a Web OAuth client.
            if (!json.RootElement.TryGetProperty("installed", out var installed)
                || installed.ValueKind != JsonValueKind.Object
                || !installed.TryGetProperty("client_id", out var idProperty)
                || idProperty.ValueKind != JsonValueKind.String
                || !installed.TryGetProperty("client_secret", out var secretProperty)
                || secretProperty.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Google OAuth JSON에는 Desktop app의 installed.client_id 및 installed.client_secret이 필요합니다.");
            var id = idProperty.GetString() ?? "";
            var secret = secretProperty.GetString() ?? "";
            if (!IsValidClientId(id) || string.IsNullOrWhiteSpace(secret))
                throw new InvalidDataException("Google OAuth JSON의 Desktop 클라이언트 ID 또는 client_secret 값이 올바르지 않습니다.");
            return (id, secret);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Google OAuth 설정 JSON이 올바르지 않습니다. 데스크톱 앱 인증 정보인지 확인하세요.", ex);
        }
    }
}
