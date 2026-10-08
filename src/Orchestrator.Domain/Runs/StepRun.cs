using Orchestrator.Domain.Common;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Domain.Runs;

/// <summary>
/// Execution state of one step inside a <see cref="WorkflowRun"/>. Mutated only through the aggregate root,
/// except for the worker's atomic claim and lease heartbeat, which are conditional SQL updates guarded by
/// <see cref="Version"/> (the fencing token).
/// </summary>
public sealed class StepRun
{
    public const int MaxErrorLength = 4000;
    public const int MaxLeaseOwnerLength = 200;

    private StepRun()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkflowRunId { get; private set; }

    public string StepKey { get; private set; } = string.Empty;

    public int Ordinal { get; private set; }

    public StepRunStatus Status { get; private set; }

    /// <summary>Number of forward attempts started in the current round (reset by an operator retry).</summary>
    public int Attempt { get; private set; }

    /// <summary>Number of compensation attempts started (reset by an operator requeue).</summary>
    public int CompensationAttempt { get; private set; }

    /// <summary>When the step becomes claimable (Ready / Retrying / Compensating).</summary>
    public DateTime? NextAttemptAt { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseExpiresAt { get; private set; }

    public string? OutputJson { get; private set; }

    public string? LastError { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    /// <summary>Duration of the last finished attempt in milliseconds.</summary>
    public long? DurationMs { get; private set; }

    /// <summary>Optimistic concurrency token / lease fencing token. Regenerated on every change.</summary>
    public Guid Version { get; private set; }

    public bool IsLeased => LeaseOwner is not null;

    internal static StepRun Create(Guid workflowRunId, StepDefinition definition) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkflowRunId = workflowRunId,
            StepKey = definition.Key,
            Ordinal = definition.Ordinal,
            Status = StepRunStatus.Pending,
            Version = Guid.NewGuid(),
        };

    internal void MarkReady(DateTime now)
    {
        TransitionTo(StepRunStatus.Ready);
        NextAttemptAt = now;
    }

    /// <summary>In-memory equivalent of the worker's SQL claim (<c>Ready|Retrying -> Running</c>).</summary>
    internal void ClaimForExecution(string owner, DateTime leaseExpiresAt, DateTime now)
    {
        TransitionTo(StepRunStatus.Running);
        Attempt++;
        LeaseOwner = owner;
        LeaseExpiresAt = leaseExpiresAt;
        StartedAt ??= now;
    }

    /// <summary>In-memory equivalent of the worker's SQL claim of a compensation.</summary>
    internal void ClaimForCompensation(string owner, DateTime leaseExpiresAt)
    {
        if (Status != StepRunStatus.Compensating || IsLeased)
        {
            throw new DomainException($"Step '{StepKey}' is not awaiting compensation.");
        }

        CompensationAttempt++;
        LeaseOwner = owner;
        LeaseExpiresAt = leaseExpiresAt;
        Version = Guid.NewGuid();
    }

    internal void Succeed(string? outputJson, long durationMs, DateTime now)
    {
        TransitionTo(StepRunStatus.Succeeded);
        OutputJson = outputJson;
        LastError = null;
        DurationMs = durationMs;
        CompletedAt = now;
        NextAttemptAt = null;
        ClearLease();
    }

    internal void ScheduleRetry(string error, DateTime retryAt, long? durationMs)
    {
        TransitionTo(StepRunStatus.Retrying);
        LastError = Truncate(error);
        NextAttemptAt = retryAt;
        DurationMs = durationMs ?? DurationMs;
        ClearLease();
    }

    internal void Fail(string error, long? durationMs, DateTime now)
    {
        TransitionTo(StepRunStatus.Failed);
        LastError = Truncate(error);
        DurationMs = durationMs ?? DurationMs;
        CompletedAt = now;
        NextAttemptAt = null;
        ClearLease();
    }

    internal void Skip(DateTime now)
    {
        TransitionTo(StepRunStatus.Skipped);
        CompletedAt = now;
        NextAttemptAt = null;
        ClearLease();
    }

    internal void Cancel(DateTime now, string reason)
    {
        TransitionTo(StepRunStatus.Cancelled);
        LastError = Truncate(reason);
        CompletedAt = now;
        NextAttemptAt = null;
        ClearLease();
    }

    /// <summary>Gives an interrupted attempt back (graceful shutdown): it does not count against the budget.</summary>
    internal void Release(DateTime now)
    {
        TransitionTo(StepRunStatus.Retrying);
        Attempt = Math.Max(0, Attempt - 1);
        NextAttemptAt = now;
        ClearLease();
    }

    internal void BeginCompensation(DateTime now)
    {
        TransitionTo(StepRunStatus.Compensating);
        CompensationAttempt = 0;
        NextAttemptAt = now;
        ClearLease();
    }

    internal void CompleteCompensation(DateTime now)
    {
        TransitionTo(StepRunStatus.Compensated);
        CompletedAt = now;
        NextAttemptAt = null;
        ClearLease();
    }

    internal void ScheduleCompensationRetry(string error, DateTime retryAt)
    {
        if (Status != StepRunStatus.Compensating)
        {
            throw new InvalidStateTransitionException("step run", Status.ToString(), StepRunStatus.Compensating.ToString());
        }

        LastError = Truncate(error);
        NextAttemptAt = retryAt;
        ClearLease();
        Version = Guid.NewGuid();
    }

    internal void ReleaseCompensation(DateTime now)
    {
        if (Status != StepRunStatus.Compensating)
        {
            throw new InvalidStateTransitionException("step run", Status.ToString(), StepRunStatus.Compensating.ToString());
        }

        CompensationAttempt = Math.Max(0, CompensationAttempt - 1);
        NextAttemptAt = now;
        ClearLease();
        Version = Guid.NewGuid();
    }

    internal void FailCompensation(string error, DateTime now)
    {
        TransitionTo(StepRunStatus.CompensationFailed);
        LastError = Truncate(error);
        CompletedAt = now;
        NextAttemptAt = null;
        ClearLease();
    }

    internal void RequeueCompensation(DateTime now)
    {
        TransitionTo(StepRunStatus.Compensating);
        CompensationAttempt = 0;
        NextAttemptAt = now;
        CompletedAt = null;
        ClearLease();
    }

    internal void ResetForRetry()
    {
        TransitionTo(StepRunStatus.Pending);
        Attempt = 0;
        CompensationAttempt = 0;
        NextAttemptAt = null;
        OutputJson = null;
        LastError = null;
        StartedAt = null;
        CompletedAt = null;
        DurationMs = null;
        ClearLease();
    }

    private void TransitionTo(StepRunStatus target)
    {
        StepStateMachine.EnsureCanTransition(Status, target);
        Status = target;
        Version = Guid.NewGuid();
    }

    private void ClearLease()
    {
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    private static string Truncate(string value) => DomainJson.Truncate(value, MaxErrorLength);
}
