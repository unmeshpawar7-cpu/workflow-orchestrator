using Orchestrator.Domain.Common;

namespace Orchestrator.Domain.Runs;

/// <summary>
/// Transition table for <see cref="WorkflowRunStatus"/>:
/// <code>
/// Pending      -> Running | Cancelled
/// Running      -> Succeeded | Failed | Cancelled
/// Failed       -> Compensating | Running (operator retry)
/// Compensating -> Compensated | DeadLettered
/// DeadLettered -> Compensating (operator requeue) | Running (operator retry)
/// Succeeded, Compensated, Cancelled are terminal.
/// </code>
/// </summary>
public static class RunStateMachine
{
    private static readonly IReadOnlyDictionary<WorkflowRunStatus, WorkflowRunStatus[]> Transitions =
        new Dictionary<WorkflowRunStatus, WorkflowRunStatus[]>
        {
            [WorkflowRunStatus.Pending] = new[] { WorkflowRunStatus.Running, WorkflowRunStatus.Cancelled },
            [WorkflowRunStatus.Running] = new[] { WorkflowRunStatus.Succeeded, WorkflowRunStatus.Failed, WorkflowRunStatus.Cancelled },
            [WorkflowRunStatus.Failed] = new[] { WorkflowRunStatus.Compensating, WorkflowRunStatus.Running },
            [WorkflowRunStatus.Compensating] = new[] { WorkflowRunStatus.Compensated, WorkflowRunStatus.DeadLettered },
            [WorkflowRunStatus.DeadLettered] = new[] { WorkflowRunStatus.Compensating, WorkflowRunStatus.Running },
            [WorkflowRunStatus.Succeeded] = Array.Empty<WorkflowRunStatus>(),
            [WorkflowRunStatus.Compensated] = Array.Empty<WorkflowRunStatus>(),
            [WorkflowRunStatus.Cancelled] = Array.Empty<WorkflowRunStatus>(),
        };

    public static bool CanTransition(WorkflowRunStatus from, WorkflowRunStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyCollection<WorkflowRunStatus> AllowedTargets(WorkflowRunStatus from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : Array.Empty<WorkflowRunStatus>();

    public static bool IsTerminal(WorkflowRunStatus status) => AllowedTargets(status).Count == 0;

    public static void EnsureCanTransition(WorkflowRunStatus from, WorkflowRunStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException("workflow run", from.ToString(), to.ToString());
        }
    }
}
