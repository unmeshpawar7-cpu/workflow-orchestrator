using System.Text.Json.Nodes;
using Orchestrator.Domain.Common;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Domain.Runs;

/// <summary>
/// Aggregate root for one execution of a workflow definition. Owns its <see cref="StepRun"/>s and is the only
/// place where run/step state transitions, dependency promotion and saga compensation ordering are decided.
/// <para>
/// <see cref="Reconcile"/> is level-triggered and idempotent: it looks at the current step states and moves
/// the run forward. Workers call it after recording every step result (fast path) and the scheduler calls it
/// for idle runs (safety net), so a crash between the two never strands a run.
/// </para>
/// Every mutation regenerates <see cref="Version"/>, so concurrent writers to the same run are serialised by
/// optimistic concurrency.
/// </summary>
public sealed class WorkflowRun
{
    public const int MaxErrorLength = 4000;
    public const int MaxIdempotencyKeyLength = 200;
    public const int MaxCorrelationIdLength = 100;

    private readonly List<StepRun> _steps = new();
    private readonly List<ExecutionEvent> _pendingEvents = new();

    private WorkflowRun()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkflowDefinitionId { get; private set; }

    /// <summary>Set when the run was materialised by a schedule.</summary>
    public Guid? ScheduleId { get; private set; }

    public WorkflowRunStatus Status { get; private set; }

    public string InputJson { get; private set; } = "{}";

    public string? OutputJson { get; private set; }

    public string? IdempotencyKey { get; private set; }

    /// <summary>SHA-256 of the canonical request payload; compared on idempotent replays.</summary>
    public string? RequestHash { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string? Error { get; private set; }

    public DateTime ScheduledFor { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public Guid Version { get; private set; }

    public IReadOnlyCollection<StepRun> Steps => _steps;

    public static WorkflowRun Create(
        WorkflowDefinition definition,
        string? inputJson,
        string correlationId,
        DateTime scheduledFor,
        DateTime now,
        string? idempotencyKey = null,
        string? requestHash = null,
        Guid? scheduleId = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Steps.Count == 0)
        {
            throw new DomainException("A workflow run needs at least one step.");
        }

        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > MaxCorrelationIdLength)
        {
            throw new DomainException($"Correlation id is required and must be at most {MaxCorrelationIdLength} characters.");
        }

        if (idempotencyKey is { Length: > MaxIdempotencyKeyLength })
        {
            throw new DomainException($"Idempotency key must be at most {MaxIdempotencyKeyLength} characters.");
        }

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition.Id,
            ScheduleId = scheduleId,
            Status = WorkflowRunStatus.Pending,
            InputJson = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            ScheduledFor = scheduledFor,
            CreatedAt = now,
            Version = Guid.NewGuid(),
        };

        foreach (var step in definition.Steps.OrderBy(s => s.Ordinal))
        {
            run._steps.Add(StepRun.Create(run.Id, step));
        }

        run.Raise(
            ExecutionEventType.RunCreated,
            $"Run created for workflow '{definition.Name}' v{definition.Version}.",
            now,
            data: new { scheduledFor, correlationId });
        return run;
    }

    /// <summary>Returns and clears the events raised since the last call (persisted by the DbContext).</summary>
    public IReadOnlyList<ExecutionEvent> DequeueEvents()
    {
        var events = _pendingEvents.ToList();
        _pendingEvents.Clear();
        return events;
    }

    public StepRun GetStep(string stepKey) =>
        _steps.FirstOrDefault(s => s.StepKey == stepKey)
        ?? throw new DomainException($"Run {Id} has no step '{stepKey}'.");

    public void Start(WorkflowDefinition definition, DateTime now)
    {
        EnsureDefinition(definition);
        TransitionTo(WorkflowRunStatus.Running);
        StartedAt = now;
        Raise(ExecutionEventType.RunStarted, "Run started.", now);
        Reconcile(definition, now);
    }

    /// <summary>
    /// Cancels a Pending/Running run. Waiting steps are cancelled immediately; steps already Running are
    /// cancelled cooperatively by their worker (it observes the run status on its next heartbeat).
    /// </summary>
    public void Cancel(DateTime now, string? reason = null)
    {
        TransitionTo(WorkflowRunStatus.Cancelled);
        var message = string.IsNullOrWhiteSpace(reason) ? "Cancelled by operator." : reason;
        foreach (var step in _steps.Where(s => s.Status is StepRunStatus.Pending or StepRunStatus.Ready or StepRunStatus.Retrying))
        {
            step.Cancel(now, message);
            Raise(ExecutionEventType.StepCancelled, $"Step '{step.StepKey}' cancelled.", now, step.StepKey);
        }

        CompletedAt = now;
        Error = DomainJson.Truncate(message, MaxErrorLength);
        Raise(ExecutionEventType.RunCancelled, message, now);
    }

    /// <summary>
    /// Forward recovery for Failed / DeadLettered runs: every step that does not hold a successful result,
    /// plus everything downstream of it, is reset and executed again with a fresh retry budget.
    /// </summary>
    public void Retry(WorkflowDefinition definition, DateTime now)
    {
        EnsureDefinition(definition);
        if (Status is not (WorkflowRunStatus.Failed or WorkflowRunStatus.DeadLettered))
        {
            throw new InvalidStateTransitionException("workflow run", Status.ToString(), WorkflowRunStatus.Running.ToString());
        }

        if (_steps.Any(s => s.Status is StepRunStatus.Running or StepRunStatus.Compensating))
        {
            throw new DomainException("The run still has steps in flight; retry once they have settled.");
        }

        var toReset = _steps.Where(s => s.Status != StepRunStatus.Succeeded).Select(s => s.StepKey).ToHashSet(StringComparer.Ordinal);
        toReset.UnionWith(WorkflowGraph.GetDescendants(definition.ToGraph(), toReset));
        foreach (var step in _steps.Where(s => toReset.Contains(s.StepKey)))
        {
            step.ResetForRetry();
        }

        TransitionTo(WorkflowRunStatus.Running);
        CompletedAt = null;
        Error = null;
        OutputJson = null;
        Raise(ExecutionEventType.RunRetried, $"Run retried by operator; {toReset.Count} step(s) reset.", now, data: new { steps = toReset.OrderBy(k => k).ToArray() });
        Reconcile(definition, now);
    }

    /// <summary>Backward recovery for DeadLettered runs: failed compensations get a fresh budget.</summary>
    public void RequeueCompensation(WorkflowDefinition definition, DateTime now)
    {
        EnsureDefinition(definition);
        if (Status != WorkflowRunStatus.DeadLettered)
        {
            throw new InvalidStateTransitionException("workflow run", Status.ToString(), WorkflowRunStatus.Compensating.ToString());
        }

        TransitionTo(WorkflowRunStatus.Compensating);
        foreach (var step in _steps.Where(s => s.Status == StepRunStatus.CompensationFailed))
        {
            step.RequeueCompensation(now);
            Raise(ExecutionEventType.StepCompensating, $"Compensation of '{step.StepKey}' requeued.", now, step.StepKey);
        }

        CompletedAt = null;
        Raise(ExecutionEventType.RunRequeued, "Dead-lettered run requeued for compensation.", now);
        Reconcile(definition, now);
    }

    public void RecordStepStarted(string stepKey, int attempt, bool compensation, string workerId, DateTime now)
    {
        Raise(
            compensation ? ExecutionEventType.StepCompensationStarted : ExecutionEventType.StepStarted,
            compensation
                ? $"Compensation of '{stepKey}' started (attempt {attempt})."
                : $"Step '{stepKey}' started (attempt {attempt}).",
            now,
            stepKey,
            new { attempt, workerId });
    }

    public void RecordStepSucceeded(WorkflowDefinition definition, string stepKey, string? outputJson, long durationMs, DateTime now)
    {
        EnsureDefinition(definition);
        var step = GetStep(stepKey);
        step.Succeed(outputJson, durationMs, now);
        Raise(ExecutionEventType.StepSucceeded, $"Step '{stepKey}' succeeded on attempt {step.Attempt}.", now, stepKey, new { attempt = step.Attempt, durationMs });
        Touch();
        Reconcile(definition, now);
    }

    /// <param name="retryAt">When to retry, or <c>null</c> when the failure is final (budget exhausted / non-retryable).</param>
    public void RecordStepFailed(WorkflowDefinition definition, string stepKey, string error, DateTime? retryAt, long durationMs, DateTime now)
    {
        EnsureDefinition(definition);
        var step = GetStep(stepKey);
        if (retryAt is DateTime due && Status == WorkflowRunStatus.Running)
        {
            step.ScheduleRetry(error, due, durationMs);
            Raise(ExecutionEventType.StepRetryScheduled, $"Step '{stepKey}' attempt {step.Attempt} failed; retry at {due:O}. {error}", now, stepKey, new { attempt = step.Attempt, retryAt = due, durationMs });
        }
        else
        {
            step.Fail(error, durationMs, now);
            Raise(ExecutionEventType.StepFailed, $"Step '{stepKey}' failed on attempt {step.Attempt}: {error}", now, stepKey, new { attempt = step.Attempt, durationMs });
        }

        Touch();
        Reconcile(definition, now);
    }

    public void RecordStepCancelled(WorkflowDefinition definition, string stepKey, string reason, DateTime now)
    {
        EnsureDefinition(definition);
        var step = GetStep(stepKey);
        step.Cancel(now, reason);
        Raise(ExecutionEventType.StepCancelled, $"Step '{stepKey}' cancelled: {reason}", now, stepKey);
        Touch();
        Reconcile(definition, now);
    }

    /// <summary>Hands an interrupted attempt back (graceful worker shutdown) without consuming the retry budget.</summary>
    public void ReleaseStep(WorkflowDefinition definition, string stepKey, string reason, DateTime now)
    {
        EnsureDefinition(definition);
        var step = GetStep(stepKey);
        if (step.Status == StepRunStatus.Running)
        {
            if (Status == WorkflowRunStatus.Running)
            {
                step.Release(now);
            }
            else
            {
                step.Cancel(now, reason);
            }
        }
        else if (step.Status == StepRunStatus.Compensating && step.IsLeased)
        {
            step.ReleaseCompensation(now);
        }
        else
        {
            return;
        }

        Raise(ExecutionEventType.StepReleased, $"Step '{stepKey}' released: {reason}", now, stepKey);
        Touch();
        Reconcile(definition, now);
    }

    public void RecordCompensationSucceeded(WorkflowDefinition definition, string stepKey, DateTime now)
    {
        EnsureDefinition(definition);
        var step = GetStep(stepKey);
        step.CompleteCompensation(now);
        Raise(ExecutionEventType.StepCompensated, $"Step '{stepKey}' compensated.", now, stepKey, new { attempt = step.CompensationAttempt });
        Touch();
        Reconcile(definition, now);
    }

    public void RecordCompensationFailed(WorkflowDefinition definition, string stepKey, string error, DateTime? retryAt, DateTime now)
    {
        EnsureDefinition(definition);
        var step = GetStep(stepKey);
        if (retryAt is DateTime due)
        {
            step.ScheduleCompensationRetry(error, due);
            Raise(ExecutionEventType.StepCompensationRetryScheduled, $"Compensation of '{stepKey}' failed; retry at {due:O}. {error}", now, stepKey, new { attempt = step.CompensationAttempt, retryAt = due });
        }
        else
        {
            step.FailCompensation(error, now);
            Raise(ExecutionEventType.StepCompensationFailed, $"Compensation of '{stepKey}' failed permanently: {error}", now, stepKey, new { attempt = step.CompensationAttempt });
        }

        Touch();
        Reconcile(definition, now);
    }

    /// <summary>
    /// Recovers a step whose lease expired (worker crash / stall). An expired forward attempt counts as a failed
    /// attempt: it is retried (Retrying) while the budget allows, otherwise it fails.
    /// </summary>
    /// <returns><c>false</c> when there was nothing to recover (already settled by someone else).</returns>
    public bool RecoverExpiredLease(WorkflowDefinition definition, string stepKey, DateTime? retryAt, DateTime now)
    {
        EnsureDefinition(definition);
        var step = GetStep(stepKey);
        const string reason = "Lease expired before the attempt completed (worker crashed, stalled or lost connectivity).";
        if (step.Status == StepRunStatus.Running)
        {
            Raise(ExecutionEventType.StepLeaseExpired, $"Lease on '{stepKey}' held by '{step.LeaseOwner}' expired.", now, stepKey, new { attempt = step.Attempt });
            if (Status != WorkflowRunStatus.Running)
            {
                step.Cancel(now, reason + " The run is no longer running.");
            }
            else if (retryAt is DateTime due)
            {
                step.ScheduleRetry(reason, due, null);
            }
            else
            {
                step.Fail(reason, null, now);
            }
        }
        else if (step.Status == StepRunStatus.Compensating && step.IsLeased)
        {
            Raise(ExecutionEventType.StepLeaseExpired, $"Compensation lease on '{stepKey}' held by '{step.LeaseOwner}' expired.", now, stepKey, new { attempt = step.CompensationAttempt });
            if (retryAt is DateTime due)
            {
                step.ScheduleCompensationRetry(reason, due);
            }
            else
            {
                step.FailCompensation(reason, now);
            }
        }
        else
        {
            return false;
        }

        Touch();
        Reconcile(definition, now);
        return true;
    }

    /// <summary>
    /// Moves the run forward based on current step states. Safe to call any number of times.
    /// </summary>
    public void Reconcile(WorkflowDefinition definition, DateTime now)
    {
        EnsureDefinition(definition);
        switch (Status)
        {
            case WorkflowRunStatus.Running:
                ReconcileRunning(definition, now);
                break;
            case WorkflowRunStatus.Failed:
                ReconcileFailed(definition, now);
                break;
            case WorkflowRunStatus.Compensating:
                ReconcileCompensating(definition, now);
                break;
        }
    }

    private void ReconcileRunning(WorkflowDefinition definition, DateTime now)
    {
        var failed = _steps.FirstOrDefault(s => s.Status is StepRunStatus.Failed or StepRunStatus.Cancelled);
        if (failed is not null)
        {
            foreach (var step in _steps.Where(s => s.Status is StepRunStatus.Pending or StepRunStatus.Ready or StepRunStatus.Retrying))
            {
                step.Skip(now);
                Raise(ExecutionEventType.StepSkipped, $"Step '{step.StepKey}' skipped because the run failed.", now, step.StepKey);
            }

            TransitionTo(WorkflowRunStatus.Failed);
            CompletedAt = now;
            Error = DomainJson.Truncate($"Step '{failed.StepKey}' failed after {failed.Attempt} attempt(s): {failed.LastError}", MaxErrorLength);
            Raise(ExecutionEventType.RunFailed, Error, now, failed.StepKey);
            ReconcileFailed(definition, now);
            return;
        }

        foreach (var step in _steps.Where(s => s.Status == StepRunStatus.Pending).OrderBy(s => s.Ordinal).ToList())
        {
            var dependencies = definition.GetStep(step.StepKey).DependsOn;
            if (dependencies.All(d => GetStep(d).Status == StepRunStatus.Succeeded))
            {
                step.MarkReady(now);
                Raise(ExecutionEventType.StepReady, $"Step '{step.StepKey}' is ready.", now, step.StepKey);
                Touch();
            }
        }

        if (_steps.All(s => s.Status == StepRunStatus.Succeeded))
        {
            OutputJson = BuildOutput(definition);
            TransitionTo(WorkflowRunStatus.Succeeded);
            CompletedAt = now;
            Raise(ExecutionEventType.RunSucceeded, "Run succeeded.", now);
        }
    }

    private void ReconcileFailed(WorkflowDefinition definition, DateTime now)
    {
        var needsCompensation = _steps.Any(s => s.Status == StepRunStatus.Succeeded && definition.GetStep(s.StepKey).HasCompensation);
        if (!needsCompensation)
        {
            return;
        }

        TransitionTo(WorkflowRunStatus.Compensating);
        CompletedAt = null;
        Raise(ExecutionEventType.CompensationStarted, "Compensating succeeded steps in reverse topological order.", now);
        ReconcileCompensating(definition, now);
    }

    private void ReconcileCompensating(WorkflowDefinition definition, DateTime now)
    {
        // In-flight forward attempts (other branches) must settle first: if they succeed they need compensating too.
        if (_steps.Any(s => s.Status == StepRunStatus.Running))
        {
            return;
        }

        var failedCompensation = _steps.FirstOrDefault(s => s.Status == StepRunStatus.CompensationFailed);
        if (failedCompensation is not null)
        {
            TransitionTo(WorkflowRunStatus.DeadLettered);
            CompletedAt = now;
            Error = DomainJson.Truncate($"Compensation of step '{failedCompensation.StepKey}' failed: {failedCompensation.LastError}", MaxErrorLength);
            Raise(ExecutionEventType.RunDeadLettered, Error, now, failedCompensation.StepKey);
            return;
        }

        // Strictly one compensation at a time, in reverse topological order.
        if (_steps.Any(s => s.Status == StepRunStatus.Compensating))
        {
            return;
        }

        var next = _steps
            .Where(s => s.Status == StepRunStatus.Succeeded && definition.GetStep(s.StepKey).HasCompensation)
            .OrderByDescending(s => s.Ordinal)
            .FirstOrDefault();
        if (next is not null)
        {
            next.BeginCompensation(now);
            Raise(ExecutionEventType.StepCompensating, $"Compensation of '{next.StepKey}' scheduled.", now, next.StepKey);
            Touch();
            return;
        }

        TransitionTo(WorkflowRunStatus.Compensated);
        CompletedAt = now;
        Raise(ExecutionEventType.RunCompensated, "All compensations completed.", now);
    }

    private string BuildOutput(WorkflowDefinition definition)
    {
        var output = new JsonObject();
        foreach (var stepDefinition in definition.Steps.Where(s => definition.IsSink(s.Key)).OrderBy(s => s.Ordinal))
        {
            var json = GetStep(stepDefinition.Key).OutputJson;
            output[stepDefinition.Key] = json is null ? null : JsonNode.Parse(json);
        }

        return output.ToJsonString();
    }

    private void EnsureDefinition(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Id != WorkflowDefinitionId)
        {
            throw new DomainException("The supplied definition does not belong to this run.");
        }
    }

    private void TransitionTo(WorkflowRunStatus target)
    {
        RunStateMachine.EnsureCanTransition(Status, target);
        Status = target;
        Touch();
    }

    private void Touch() => Version = Guid.NewGuid();

    private void Raise(ExecutionEventType type, string message, DateTime now, string? stepKey = null, object? data = null)
    {
        _pendingEvents.Add(ExecutionEvent.Create(Id, type, message, now, stepKey, data is null ? null : DomainJson.Serialize(data)));
    }
}
