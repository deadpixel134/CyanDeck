using System;
using System.Collections.Generic;

namespace DesktopDeck.Automation;

/// <summary>
/// Session-local guard against duplicate FileSystemWatcher/reconcile notifications.
/// Seed with the host's managed desktop baseline before starting watchers.
/// </summary>
public sealed class DesktopFirstSeenGate
{
    private readonly HashSet<string> _knownKeys;

    public DesktopFirstSeenGate(IEnumerable<string> baselineIdentityKeys, StringComparer? comparer = null)
    {
        _knownKeys = new HashSet<string>(comparer ?? StringComparer.OrdinalIgnoreCase);
        if (baselineIdentityKeys is null)
            return;

        foreach (var key in baselineIdentityKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                _knownKeys.Add(key);
        }
    }

    public bool MarkAndIsNew(string identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey))
            return false;

        return _knownKeys.Add(identityKey);
    }

    public void MarkKnown(string identityKey)
    {
        if (!string.IsNullOrWhiteSpace(identityKey))
            _knownKeys.Add(identityKey);
    }

    public void Forget(string identityKey)
    {
        if (!string.IsNullOrWhiteSpace(identityKey))
            _knownKeys.Remove(identityKey);
    }

    public void RenameKnown(string oldIdentityKey, string newIdentityKey)
    {
        var wasKnown = !string.IsNullOrWhiteSpace(oldIdentityKey) && _knownKeys.Remove(oldIdentityKey);
        if (wasKnown) MarkKnown(newIdentityKey);
    }
}
