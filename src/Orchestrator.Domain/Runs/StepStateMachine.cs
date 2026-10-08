using Orchestrator.Domain.Common;

namespace Orchestrator.Domain.Runs;

/// <summary>
/// Transition table for <see cref="StepRunStatus"/>:
/// <code>
/// Pending            -> Ready | Skipped | Cancelled
/// Ready              -> Running | Skipped | Cancelled
/// Running            -> Succeeded | Failed | Retrying | Cancelled
/// Retrying           -> Running | Skipped | Cancelled
/// Succeeded          -> Compensating | Pending (operator retry of a dead-lettered run)
/// Compensating       -> Compensated | CompensationFailed
/// CompensationFailed -> Compensating (operator requeue) | Pending (operator retry)
/// Failed, Skipped, Cancelled, Compensated -> Pending (operator retry)
/// </code>
/// The worker's atomic claim (<c>Ready|Retrying -> Running</c>) is executed as a conditional SQL UPDATE for
/// atomicity; it encodes exactly the <c>Ready -> Running</c> and <c>Retrying -> Running</c> edges above.
/// </summary>
public static class StepStateMachine
{
    private static readonly IReadOnlyDictionary<StepRunStatus, StepRunStatus[]> Transitions =
        new Dictionary<StepRunStatus, StepRunStatus[]>
        {
            [StepRunStatus.Pending] = new[] { StepRunStatus.Ready, StepRunStatus.Skipped, StepRunStatus.Cancelled },
            [StepRunStatus.Ready] = new[] { StepRunStatus.Running, StepRunStatus.Skipped, StepRunStatus.Cancelled },
            [StepRunStatus.Running] = new[] { StepRunStatus.Succeeded, StepRunStatus.Failed, StepRunStatus.Retrying, StepRunStatus.Cancelled },
            [StepRunStatus.Retrying] = new[] { StepRunStatus.Running, StepRunStatus.Skipped, StepRunStatus.Cancelled },
            [StepRunStatus.Succeeded] = new[] { StepRunStatus.Compensating, StepRunStatus.Pending },
            [StepRunStatus.Failed] = new[] { StepRunStatus.Pending },
            [StepRunStatus.Skipped] = new[] { StepRunStatus.Pending },
            [StepRunStatus.Cancelled] = new[] { StepRunStatus.Pending },
            [StepRunStatus.Compensating] = new[] { StepRunStatus.Compensated, StepRunStatus.CompensationFailed },
            [StepRunStatus.Compensated] = new[] { StepRunStatus.Pending },
            [StepRunStatus.CompensationFailed] = new[] { StepRunStatus.Compensating, StepRunStatus.Pending },
        };

    public static bool CanTransition(StepRunStatus from, StepRunStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyCollection<StepRunStatus> AllowedTargets(StepRunStatus from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : Array.Empty<StepRunStatus>();

    public static void EnsureCanTransition(StepRunStatus from, StepRunStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException("step run", from.ToString(), to.ToString());
        }
    }
}
