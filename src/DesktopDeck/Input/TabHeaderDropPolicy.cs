using System;

namespace DesktopDeck.Input;

/// <summary>
/// Framework-neutral validation helpers for tab-header drops.
/// The host owns payload interpretation and the actual model/filesystem command.
/// </summary>
public static class TabHeaderDropPolicy
{
    public static TabHeaderDropEvaluation NormalizeEvaluation(
        TabHeaderDropTarget target,
        TabHeaderDropContext context,
        TabHeaderDropEvaluation evaluation)
    {
        if (!target.IsValid || !evaluation.IsValidFor(context))
        {
            return TabHeaderDropEvaluation.Reject;
        }

        return evaluation;
    }

    public static bool ShouldActivateAfterCommit(
        TabHeaderDropEvaluation evaluation,
        bool commitSucceeded)
    {
        return commitSucceeded && evaluation.Accepted && evaluation.ActivateTabAfterCommit;
    }
}
