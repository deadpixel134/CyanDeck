using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopDeck.QuickHide;

/// <summary>
/// Transaction helper for changing the Quick-hide Enabled setting.
/// The host persists the returned ValueToPersist only when the status is ReadyToPersist.
/// </summary>
public sealed class QuickHideFeatureController
{
    private readonly Func<QuickHideRuntimeSnapshot> _readState;
    private readonly Func<QuickHideShowReason, CancellationToken, Task<bool>> _ensureVisibleAsync;

    public QuickHideFeatureController(
        Func<QuickHideRuntimeSnapshot> readState,
        Func<QuickHideShowReason, CancellationToken, Task<bool>> ensureVisibleAsync)
    {
        _readState = readState ?? throw new ArgumentNullException(nameof(readState));
        _ensureVisibleAsync = ensureVisibleAsync ?? throw new ArgumentNullException(nameof(ensureVisibleAsync));
    }

    public async Task<QuickHideEnabledChangeResult> PrepareEnabledChangeAsync(
        bool currentEnabled,
        bool requestedEnabled,
        CancellationToken cancellationToken = default)
    {
        var state = _readState();
        var plan = QuickHideFeaturePolicy.PlanEnabledChange(
            currentEnabled,
            requestedEnabled,
            state.IsQuickHideActive,
            state.HasHiddenVisualResidue);

        switch (plan)
        {
            case QuickHideEnabledChangePlan.NoChange:
                return new QuickHideEnabledChangeResult(
                    QuickHideEnabledChangeStatus.NoChange,
                    currentEnabled,
                    VisibilityWasNormalized: false);

            case QuickHideEnabledChangePlan.PersistOnly:
                return new QuickHideEnabledChangeResult(
                    QuickHideEnabledChangeStatus.ReadyToPersist,
                    requestedEnabled,
                    VisibilityWasNormalized: false);

            case QuickHideEnabledChangePlan.ShowThenPersistDisabled:
                try
                {
                    var visible = await _ensureVisibleAsync(
                        QuickHideShowReason.UserDisabledFeature,
                        cancellationToken).ConfigureAwait(true);

                    if (!visible)
                    {
                        return new QuickHideEnabledChangeResult(
                            QuickHideEnabledChangeStatus.ShowFailedDoNotPersist,
                            currentEnabled,
                            VisibilityWasNormalized: false);
                    }

                    return new QuickHideEnabledChangeResult(
                        QuickHideEnabledChangeStatus.ReadyToPersist,
                        requestedEnabled,
                        VisibilityWasNormalized: true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Do not persist Disabled if the visible-state repair failed.
                    return new QuickHideEnabledChangeResult(
                        QuickHideEnabledChangeStatus.ShowFailedDoNotPersist,
                        currentEnabled,
                        VisibilityWasNormalized: false);
                }

            default:
                throw new InvalidOperationException($"Unsupported Quick-hide change plan: {plan}");
        }
    }
}
