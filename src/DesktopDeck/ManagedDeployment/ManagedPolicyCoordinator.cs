using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopDeck.ManagedDeployment;

public sealed class ManagedPolicyCoordinator
{
    private readonly ManagedPolicyReader _reader;
    private readonly ManagedPolicyPlanner _planner;
    private readonly IManagedDeploymentAdapter _adapter;
    private readonly ManagedPolicyCache? _cache;
    private readonly Action<string>? _diagnostic;
    private readonly object _gate = new();

    private string? _activeHash;
    private HashSet<string> _lockedSettings = new(StringComparer.Ordinal);
    private Dictionary<string, bool> _capabilities = new(StringComparer.Ordinal);
    private ManagedDeploymentStyle? _activeStyle;
    private ManagedPolicySourceKind? _activeSourceKind;
    private ManagedPolicySnapshot? _activeSnapshot;

    public ManagedPolicyCoordinator(
        ManagedPolicyReader reader,
        ManagedPolicyPlanner planner,
        IManagedDeploymentAdapter adapter,
        ManagedPolicyCache? cache = null,
        Action<string>? diagnostic = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _cache = cache;
        _diagnostic = diagnostic;
    }

    public string? ActivePolicySha256 { get { lock (_gate) return _activeHash; } }
    public ManagedDeploymentStyle? ActiveStyle { get { lock (_gate) return _activeStyle; } }
    public ManagedPolicySourceKind? ActiveSourceKind { get { lock (_gate) return _activeSourceKind; } }

    public ManagedPolicyDiagnosticSummary GetDiagnosticSummary()
    {
        lock (_gate)
        {
            return new ManagedPolicyDiagnosticSummary(
                _activeHash is not null,
                _activeStyle,
                _activeSourceKind,
                _activeHash is { Length: >= 12 } ? _activeHash[..12] : _activeHash,
                _lockedSettings.Count,
                _capabilities.Count);
        }
    }

    public bool IsSettingLocked(string key)
    {
        lock (_gate) return _lockedSettings.Contains(key);
    }

    public bool IsCapabilityAllowed(string capability)
    {
        lock (_gate) return !_capabilities.TryGetValue(capability, out var allowed) || allowed;
    }

    public ManagedPolicyApplyResult ReloadFromSource(string path, bool allowCacheFallback)
    {
        var load = _reader.Load(path);
        if (!load.Success || load.Snapshot is null)
        {
            _diagnostic?.Invoke("Managed deployment source reload failed; current runtime policy remains unchanged.");
            if (allowCacheFallback && ActivePolicySha256 is null && _cache is not null)
            {
                var cached = _cache.TryLoad(_reader);
                if (cached.Success && cached.Snapshot is not null)
                    return ApplySnapshot(cached.Snapshot, persistCache: false);
            }
            return ManagedPolicyApplyResult.Failed(load.Error ?? "Managed policy load failed.", ActivePolicySha256);
        }
        return ApplySnapshot(load.Snapshot, persistCache: true);
    }

    public ManagedPolicyApplyResult ApplySnapshot(ManagedPolicySnapshot snapshot, bool persistCache)
        => ApplySnapshot(snapshot, persistCache, forceApply: false);

    public ManagedPolicyApplyResult ReapplyActivePolicy()
    {
        ManagedPolicySnapshot? snapshot;
        lock (_gate) snapshot = _activeSnapshot;
        return snapshot is null
            ? ManagedPolicyApplyResult.NoChange(ActivePolicySha256)
            : ApplySnapshot(snapshot, persistCache: false, forceApply: true);
    }

    private ManagedPolicyApplyResult ApplySnapshot(ManagedPolicySnapshot snapshot, bool persistCache, bool forceApply)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (!forceApply && string.Equals(_activeHash, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                return ManagedPolicyApplyResult.NoChange(_activeHash);

            if (!_planner.TryBuildPlan(snapshot, _adapter, out var plan, out var planError) || plan is null)
            {
                _diagnostic?.Invoke("Managed deployment validation failed; previous policy remains active.");
                return ManagedPolicyApplyResult.Failed(planError ?? "Managed policy validation failed.", _activeHash);
            }

            var rollbackValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var setting in plan.ValuesToApply)
                rollbackValues[setting.Key] = _adapter.CaptureSettingJson(setting.Key);

            var previousLocks = new HashSet<string>(_lockedSettings, StringComparer.Ordinal);
            var previousCapabilities = new Dictionary<string, bool>(_capabilities, StringComparer.Ordinal);
            var previousHash = _activeHash;
            var previousStyle = _activeStyle;
            var previousSource = _activeSourceKind;
            var previousSnapshot = _activeSnapshot;

            try
            {
                foreach (var setting in plan.ValuesToApply)
                    _adapter.ApplySettingJson(setting.Key, setting.CanonicalJson);

                _lockedSettings = new HashSet<string>(plan.LockedSettings, StringComparer.Ordinal);
                _capabilities = new Dictionary<string, bool>(plan.Capabilities, StringComparer.Ordinal);
                _activeHash = plan.PolicySha256;
                _activeStyle = plan.DeploymentStyle;
                _activeSourceKind = snapshot.SourceKind;
                _activeSnapshot = snapshot;

                _adapter.RefreshManagedPolicyUi();
                _adapter.SaveState();

                if (persistCache && _cache is not null)
                {
                    try { _cache.Store(snapshot.RawUtf8); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _diagnostic?.Invoke($"Managed deployment cache write failed: {ex.GetType().Name}");
                    }
                }

                _diagnostic?.Invoke($"Managed deployment policy applied ({plan.DeploymentStyle}, {snapshot.SourceKind}).");
                return ManagedPolicyApplyResult.Applied(plan.PolicySha256);
            }
            catch (Exception applyError)
            {
                try
                {
                    foreach (var oldValue in rollbackValues)
                        _adapter.ApplySettingJson(oldValue.Key, oldValue.Value);
                    _lockedSettings = previousLocks;
                    _capabilities = previousCapabilities;
                    _activeHash = previousHash;
                    _activeStyle = previousStyle;
                    _activeSourceKind = previousSource;
                    _activeSnapshot = previousSnapshot;
                    _adapter.RefreshManagedPolicyUi();
                    _adapter.SaveState();
                }
                catch (Exception rollbackError)
                {
                    _diagnostic?.Invoke($"Managed deployment rollback failed: {rollbackError.GetType().Name}");
                }

                _diagnostic?.Invoke($"Managed deployment apply failed: {applyError.GetType().Name}");
                return ManagedPolicyApplyResult.Failed("Managed policy could not be applied; rollback was attempted.", previousHash);
            }
        }
    }
}
