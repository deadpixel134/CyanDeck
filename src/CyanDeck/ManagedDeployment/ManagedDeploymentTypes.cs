using System;
using System.Collections.Generic;

namespace CyanDeck.ManagedDeployment;

public enum ManagedDeploymentStyle
{
    Locked,
    Flexible,
    Unlocked
}

public enum ManagedPolicySourceKind
{
    LocalFile,
    UncFile,
    Cache
}

public sealed record ManagedPolicySetting(string Key, string CanonicalJson);

public sealed record ManagedPolicyDocument(
    int Schema,
    string Name,
    ManagedDeploymentStyle DeploymentStyle,
    IReadOnlyList<ManagedPolicySetting> Settings,
    IReadOnlySet<string> LockedSettings,
    IReadOnlyDictionary<string, bool> Capabilities);

public sealed record ManagedPolicySnapshot(
    ManagedPolicyDocument Document,
    ManagedPolicySourceKind SourceKind,
    string Sha256,
    byte[] RawUtf8);

public sealed record ManagedPolicyPlan(
    ManagedDeploymentStyle DeploymentStyle,
    IReadOnlyList<ManagedPolicySetting> ValuesToApply,
    IReadOnlySet<string> LockedSettings,
    IReadOnlyDictionary<string, bool> Capabilities,
    string PolicySha256);

public sealed record ManagedPolicyLoadResult(
    bool Success,
    ManagedPolicySnapshot? Snapshot,
    string? Error)
{
    public static ManagedPolicyLoadResult Ok(ManagedPolicySnapshot snapshot) => new(true, snapshot, null);
    public static ManagedPolicyLoadResult Fail(string error) => new(false, null, error);
}

public sealed record ManagedPolicyDiagnosticSummary(
    bool Active,
    ManagedDeploymentStyle? DeploymentStyle,
    ManagedPolicySourceKind? SourceKind,
    string? HashPrefix,
    int LockedSettingCount,
    int CapabilityOverrideCount);

public sealed record ManagedPolicyApplyResult(
    bool Success,
    bool Changed,
    string? Error,
    string? PolicySha256)
{
    public static ManagedPolicyApplyResult NoChange(string? hash) => new(true, false, null, hash);
    public static ManagedPolicyApplyResult Applied(string hash) => new(true, true, null, hash);
    public static ManagedPolicyApplyResult Failed(string error, string? hash = null) => new(false, false, error, hash);
}

public interface IManagedDeploymentAdapter
{
    IReadOnlySet<string> KnownSettings { get; }
    IReadOnlySet<string> LockableSettings { get; }
    IReadOnlySet<string> KnownCapabilities { get; }

    bool IsSettingAtDefault(string key);
    bool TryNormalizeSetting(string key, string rawJson, out string canonicalJson, out string? error);
    string CaptureSettingJson(string key);
    void ApplySettingJson(string key, string canonicalJson);
    void RefreshManagedPolicyUi();
    void SaveState();
}
