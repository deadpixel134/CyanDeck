using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopDeck.ManagedDeployment;

public sealed class ManagedPolicyPlanner
{
    public bool TryBuildPlan(
        ManagedPolicySnapshot snapshot,
        IManagedDeploymentAdapter adapter,
        out ManagedPolicyPlan? plan,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(adapter);

        var document = snapshot.Document;
        var suppliedKeys = new HashSet<string>(document.Settings.Select(s => s.Key), StringComparer.Ordinal);
        var normalized = new List<ManagedPolicySetting>(document.Settings.Count);

        foreach (var setting in document.Settings)
        {
            if (!adapter.KnownSettings.Contains(setting.Key))
            {
                plan = null;
                error = $"Unknown managed setting: {setting.Key}";
                return false;
            }
            if (!adapter.TryNormalizeSetting(setting.Key, setting.CanonicalJson, out var canonical, out var normalizeError))
            {
                plan = null;
                error = $"Invalid managed setting {setting.Key}: {normalizeError ?? "validation failed"}";
                return false;
            }
            normalized.Add(new ManagedPolicySetting(setting.Key, canonical));
        }

        foreach (var key in document.LockedSettings)
        {
            if (!suppliedKeys.Contains(key))
            {
                plan = null;
                error = $"lockedSettings key is not present in settings: {key}";
                return false;
            }
            if (!adapter.LockableSettings.Contains(key))
            {
                plan = null;
                error = $"Setting cannot be managed-locked: {key}";
                return false;
            }
        }

        foreach (var capability in document.Capabilities)
        {
            if (!adapter.KnownCapabilities.Contains(capability.Key))
            {
                plan = null;
                error = $"Unknown managed capability: {capability.Key}";
                return false;
            }
        }

        IReadOnlyList<ManagedPolicySetting> valuesToApply;
        HashSet<string> locked;
        IReadOnlyDictionary<string, bool> capabilities;

        switch (document.DeploymentStyle)
        {
            case ManagedDeploymentStyle.Locked:
                locked = new HashSet<string>(suppliedKeys, StringComparer.Ordinal);
                foreach (var key in document.LockedSettings)
                    locked.Add(key);
                foreach (var key in locked)
                {
                    if (!adapter.LockableSettings.Contains(key))
                    {
                        plan = null;
                        error = $"Locked deployment contains a non-lockable setting: {key}";
                        return false;
                    }
                }
                valuesToApply = normalized;
                capabilities = document.Capabilities;
                break;

            case ManagedDeploymentStyle.Flexible:
                locked = new HashSet<string>(document.LockedSettings, StringComparer.Ordinal);
                valuesToApply = normalized;
                capabilities = document.Capabilities;
                break;

            case ManagedDeploymentStyle.Unlocked:
                if (document.LockedSettings.Count != 0)
                {
                    plan = null;
                    error = "Unlocked deployment cannot contain lockedSettings.";
                    return false;
                }
                if (document.Capabilities.Any(kvp => !kvp.Value))
                {
                    plan = null;
                    error = "Unlocked deployment cannot deny capabilities.";
                    return false;
                }
                locked = new HashSet<string>(StringComparer.Ordinal);
                valuesToApply = normalized.Where(s => adapter.IsSettingAtDefault(s.Key)).ToArray();
                capabilities = new Dictionary<string, bool>(StringComparer.Ordinal);
                break;

            default:
                plan = null;
                error = "Unsupported deployment style.";
                return false;
        }

        plan = new ManagedPolicyPlan(document.DeploymentStyle, valuesToApply, locked, capabilities, snapshot.Sha256);
        error = null;
        return true;
    }
}
