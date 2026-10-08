namespace Orchestrator.Domain.Runs;

/// <summary>Lifecycle of a workflow run. See <see cref="RunStateMachine"/> for the legal transitions.</summary>
public enum WorkflowRunStatus
{
    /// <summary>Created and waiting for its scheduled time / a concurrency slot.</summary>
    Pending = 0,

    /// <summary>Steps are being executed.</summary>
    Running = 1,

    /// <summary>Every step succeeded. Terminal.</summary>
    Succeeded = 2,

    /// <summary>A step exhausted its retries. Compensation starts if any succeeded step has a compensation handler.</summary>
    Failed = 3,

    /// <summary>Compensation handlers of succeeded steps are running in reverse topological order.</summary>
    Compensating = 4,

    /// <summary>All required compensations succeeded. Terminal.</summary>
    Compensated = 5,

    /// <summary>A compensation handler failed permanently; operator action required (requeue or retry).</summary>
    DeadLettered = 6,

    /// <summary>Cancelled by an operator. Terminal.</summary>
    Cancelled = 7,
}
