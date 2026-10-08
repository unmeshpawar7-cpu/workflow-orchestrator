namespace Orchestrator.Domain.Runs;

/// <summary>Lifecycle of a single step inside a run. See <see cref="StepStateMachine"/>.</summary>
public enum StepRunStatus
{
    /// <summary>Waiting for its dependencies.</summary>
    Pending = 0,

    /// <summary>Dependencies satisfied; claimable by a worker.</summary>
    Ready = 1,

    /// <summary>Claimed by a worker that holds a lease on it.</summary>
    Running = 2,

    /// <summary>Handler completed successfully; output stored.</summary>
    Succeeded = 3,

    /// <summary>Handler failed and no retries remain (or the failure was non-retryable).</summary>
    Failed = 4,

    /// <summary>Last attempt failed; claimable again once <c>NextAttemptAt</c> is due.</summary>
    Retrying = 5,

    /// <summary>Never executed because the run failed first.</summary>
    Skipped = 6,

    /// <summary>Compensation is due or in progress (a lease marks in-progress).</summary>
    Compensating = 7,

    /// <summary>Compensation handler succeeded.</summary>
    Compensated = 8,

    /// <summary>Compensation handler failed permanently; the run is dead-lettered.</summary>
    CompensationFailed = 9,

    /// <summary>Stopped because the run was cancelled.</summary>
    Cancelled = 10,
}
