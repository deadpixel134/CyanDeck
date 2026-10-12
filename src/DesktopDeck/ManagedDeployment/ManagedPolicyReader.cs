using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopDeck.ManagedDeployment;

public sealed class ManagedPolicyReader
{
    public const int CurrentSchema = 1;
    public const int MaxPolicyBytes = 1024 * 1024;
    public const int MaxSettings = 128;
    public const int MaxCapabilities = 64;
    public const int MaxKeyLength = 96;
    public const int MaxNameLength = 128;

    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schema", "name", "deploymentStyle", "settings", "lockedSettings", "capabilities"
    };

    public ManagedPolicyLoadResult Load(string path, ManagedPolicySourceKind? forcedKind = null)
    {
        if (!TryClassifyPath(path, out var sourceKind, out var pathError))
            return ManagedPolicyLoadResult.Fail(pathError!);

        if (forcedKind is not null)
            sourceKind = forcedKind.Value;

        byte[] bytes;
        try
        {
            bytes = ReadBounded(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ManagedPolicyLoadResult.Fail($"Could not read managed policy: {ex.GetType().Name}");
        }

        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });

            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return ManagedPolicyLoadResult.Fail("Managed policy root must be a JSON object.");

            if (!HasUniqueKnownProperties(json.RootElement, RootProperties, out var rootError))
                return ManagedPolicyLoadResult.Fail(rootError!);

            var root = json.RootElement;
            if (!root.TryGetProperty("schema", out var schemaNode) ||
                schemaNode.ValueKind != JsonValueKind.Number ||
                !schemaNode.TryGetInt32(out var schema))
                return ManagedPolicyLoadResult.Fail("Managed policy requires an integer schema.");

            if (schema != CurrentSchema)
                return ManagedPolicyLoadResult.Fail($"Unsupported managed policy schema {schema}; expected {CurrentSchema}.");

            var name = "Managed deployment";
            if (root.TryGetProperty("name", out var nameNode))
            {
                if (nameNode.ValueKind != JsonValueKind.String)
                    return ManagedPolicyLoadResult.Fail("name must be a string.");
                name = (nameNode.GetString() ?? string.Empty).Trim();
                if (name.Length is < 1 or > MaxNameLength)
                    return ManagedPolicyLoadResult.Fail($"name must be 1-{MaxNameLength} characters.");
            }

            if (!root.TryGetProperty("deploymentStyle", out var styleNode) || styleNode.ValueKind != JsonValueKind.String)
                return ManagedPolicyLoadResult.Fail("deploymentStyle is required.");

            if (!Enum.TryParse<ManagedDeploymentStyle>(styleNode.GetString(), ignoreCase: true, out var style))
                return ManagedPolicyLoadResult.Fail("deploymentStyle must be Locked, Flexible, or Unlocked.");

            var settings = new List<ManagedPolicySetting>();
            if (root.TryGetProperty("settings", out var settingsNode))
            {
                if (settingsNode.ValueKind != JsonValueKind.Object)
                    return ManagedPolicyLoadResult.Fail("settings must be an object.");

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in settingsNode.EnumerateObject())
                {
                    if (!ValidateKey(property.Name, out var keyError))
                        return ManagedPolicyLoadResult.Fail($"settings: {keyError}");
                    if (!seen.Add(property.Name))
                        return ManagedPolicyLoadResult.Fail($"Duplicate setting key: {property.Name}");
                    if (settings.Count >= MaxSettings)
                        return ManagedPolicyLoadResult.Fail($"settings exceeds the {MaxSettings}-entry limit.");
                    settings.Add(new ManagedPolicySetting(property.Name, property.Value.GetRawText()));
                }
            }

            var locked = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("lockedSettings", out var lockedNode))
            {
                if (lockedNode.ValueKind != JsonValueKind.Array)
                    return ManagedPolicyLoadResult.Fail("lockedSettings must be an array.");
                foreach (var item in lockedNode.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return ManagedPolicyLoadResult.Fail("lockedSettings entries must be strings.");
                    var key = item.GetString() ?? string.Empty;
                    if (!ValidateKey(key, out var keyError))
                        return ManagedPolicyLoadResult.Fail($"lockedSettings: {keyError}");
                    if (!locked.Add(key))
                        return ManagedPolicyLoadResult.Fail($"Duplicate lockedSettings key: {key}");
                    if (locked.Count > MaxSettings)
                        return ManagedPolicyLoadResult.Fail($"lockedSettings exceeds the {MaxSettings}-entry limit.");
                }
            }

            var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (root.TryGetProperty("capabilities", out var capabilitiesNode))
            {
                if (capabilitiesNode.ValueKind != JsonValueKind.Object)
                    return ManagedPolicyLoadResult.Fail("capabilities must be an object.");
                foreach (var property in capabilitiesNode.EnumerateObject())
                {
                    if (!ValidateKey(property.Name, out var keyError))
                        return ManagedPolicyLoadResult.Fail($"capabilities: {keyError}");
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return ManagedPolicyLoadResult.Fail($"Capability {property.Name} must be boolean.");
                    if (!capabilities.TryAdd(property.Name, property.Value.GetBoolean()))
                        return ManagedPolicyLoadResult.Fail($"Duplicate capability key: {property.Name}");
                    if (capabilities.Count > MaxCapabilities)
                        return ManagedPolicyLoadResult.Fail($"capabilities exceeds the {MaxCapabilities}-entry limit.");
                }
            }

            var document = new ManagedPolicyDocument(schema, name, style, settings, locked, capabilities);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return ManagedPolicyLoadResult.Ok(new ManagedPolicySnapshot(document, sourceKind, hash, bytes));
        }
        catch (JsonException ex)
        {
            return ManagedPolicyLoadResult.Fail($"Invalid managed-policy JSON: {ex.Message}");
        }
    }

    public static bool TryClassifyPath(string path, out ManagedPolicySourceKind kind, out string? error)
    {
        kind = ManagedPolicySourceKind.LocalFile;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Managed policy path is empty.";
            return false;
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            error = "Managed policy must be a local or UNC file path; URL schemes are not supported.";
            return false;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            error = "Managed policy path must be fully qualified.";
            return false;
        }

        kind = path.StartsWith(@"\\", StringComparison.Ordinal) ? ManagedPolicySourceKind.UncFile : ManagedPolicySourceKind.LocalFile;
        return true;
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 32 * 1024,
            FileOptions.SequentialScan);

        if (stream.Length > MaxPolicyBytes)
            throw new IOException($"Managed policy exceeds {MaxPolicyBytes} bytes.");

        var rented = ArrayPool<byte>.Shared.Rent(MaxPolicyBytes + 1);
        try
        {
            var total = 0;
            while (total <= MaxPolicyBytes)
            {
                var read = stream.Read(rented, total, Math.Min(32 * 1024, MaxPolicyBytes + 1 - total));
                if (read == 0)
                    break;
                total += read;
            }
            if (total > MaxPolicyBytes)
                throw new IOException($"Managed policy exceeds {MaxPolicyBytes} bytes.");
            return rented.AsSpan(0, total).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static bool HasUniqueKnownProperties(JsonElement obj, IReadOnlySet<string> allowed, out string? error)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error = $"Duplicate root property: {property.Name}";
                return false;
            }
            if (!allowed.Contains(property.Name))
            {
                error = $"Unknown root property: {property.Name}";
                return false;
            }
        }
        error = null;
        return true;
    }

    private static bool ValidateKey(string key, out string? error)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
        {
            error = $"key length must be 1-{MaxKeyLength}.";
            return false;
        }
        foreach (var ch in key)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'))
            {
                error = $"invalid key '{key}'.";
                return false;
            }
        }
        error = null;
        return true;
    }
}
