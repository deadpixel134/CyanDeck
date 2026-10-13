using System;
using System.Collections.Generic;

namespace CyanDeck.FolderPortals;

public interface IPortalIconSizeHost
{
    bool IsSafeMode { get; }
    bool MasterEnabled { get; }
    bool CanEditAppearance { get; }
    bool IsSelectedTabFolderPortal { get; }
    int CurrentIconSizePixels { get; }

    void SetIconSizePixels(int pixels);
    void RefreshSelectedGroupItems();
    void SaveState();
}

public sealed class PortalViewIconSizeCoordinator
{
    private readonly IPortalIconSizeHost _host;
    private readonly PortalIconSizeMenuOptions _options;

    public PortalViewIconSizeCoordinator(
        IPortalIconSizeHost host,
        PortalIconSizeMenuOptions? options = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = (options ?? new PortalIconSizeMenuOptions()).Normalize();
    }

    public IReadOnlyList<PortalIconSizeMenuItem> BuildMenu() =>
        PortalViewIconSizePolicy.BuildMenu(BuildContext(), _options);

    public bool TryApply(int requestedPixels)
    {
        var context = BuildContext();
        if (!PortalViewIconSizePolicy.CanApply(context, requestedPixels, _options))
        {
            return false;
        }

        if (requestedPixels == _host.CurrentIconSizePixels)
        {
            return true;
        }

        var oldPixels = _host.CurrentIconSizePixels;
        try
        {
            // Reuse the same central icon-size setter used by Settings and Ctrl+wheel.
            _host.SetIconSizePixels(requestedPixels);
            _host.RefreshSelectedGroupItems();
            _host.SaveState();
            return true;
        }
        catch
        {
            // Best-effort transactional rollback. The host setter remains the single authority.
            try
            {
                _host.SetIconSizePixels(oldPixels);
                _host.RefreshSelectedGroupItems();
            }
            catch
            {
                // Host-level recovery/diagnostics owns any secondary rollback failure.
            }

            return false;
        }
    }

    private PortalIconSizeContext BuildContext() => new(
        IsFolderPortal: _host.IsSelectedTabFolderPortal,
        IsSafeMode: _host.IsSafeMode,
        MasterEnabled: _host.MasterEnabled,
        CanEditAppearance: _host.CanEditAppearance,
        CurrentPixels: _host.CurrentIconSizePixels);
}
