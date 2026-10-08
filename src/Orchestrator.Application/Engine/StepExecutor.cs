using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Common;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Application.Engine;

/// <summary>A step claimed by a worker. <see cref="Version"/> is the fencing token obtained by the claim.</summary>
public sealed record ClaimedStep(
    Guid StepRunId,
    Guid RunId,
    Guid WorkflowDefinitionId,
    string StepKey,
    bool IsCompensation,
    int Attempt,
    string WorkerId,
    Guid Version,
    DateTime LeaseExpiresAt);

public enum StepExecutionOutcome
{
    Succeeded,
    RetryScheduled,
    Failed,
    Cancelled,
    Released,
    LeaseLost,
}

public interface IStepExecutor
{
    /// <summary>Atomically claims the next due step (Ready, due Retrying, or due compensation).</summary>
    Task<ClaimedStep?> TryClaimNextAsync(string workerId, CancellationToken cancellationToken);

    /// <summary>Executes a claimed step: heartbeats, timeout, handler, then records the outcome.</summary>
    Task<StepExecutionOutcome> ExecuteAsync(ClaimedStep claim, CancellationToken stoppingToken);

    /// <summary>Returns steps whose lease expired to the queue (or fails them when out of attempts).</summary>
    Task<int> RecoverExpiredLeasesAsync(CancellationToken cancellationToken);

    /// <summary>Claim + execute inline. Used by tests and by simple single-threaded hosts.</summary>
    Task<StepExecutionOutcome?> ProcessNextAsync(string workerId, CancellationToken cancellationToken);
}

/// <summary>
/// Core of the worker. One instance per DI scope; the hosted <c>StepWorkerService</c> creates a scope per
/// claimed step so parallel executions never share a DbContext.
/// </summary>
public sealed class StepExecutor : IStepExecutor
{
    private static readonly TimeSpan RecordingTimeout = TimeSpan.FromSeconds(15);

    private readonly IOrchestratorDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStepHandlerResolver _handlers;
    private readonly TemplateResolver _templates;
    private readonly IDistributedLockProvider _locks;
    private readonly IDefinitionProvider _definitions;
    private readonly IRetryPolicyFactory _retryPolicies;
    private readonly RunMutator _mutator;
    private readonly TimeProvider _timeProvider;
    private readonly WorkerOptions _options;
    private readonly ILogger<StepExecutor> _logger;

    public StepExecutor(
        IOrchestratorDbContext db,
        IServiceScopeFactory scopeFactory,
        IStepHandlerResolver handlers,
        TemplateResolver templates,
        IDistributedLockProvider locks,
        IDefinitionProvider definitions,
        IRetryPolicyFactory retryPolicies,
        RunMutator mutator,
        TimeProvider timeProvider,
        IOptions<WorkerOptions> options,
        ILogger<StepExecutor> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _handlers = handlers;
        _templates = templates;
        _locks = locks;
        _definitions = definitions;
        _retryPolicies = retryPolicies;
        _mutator = mutator;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<ClaimedStep?> TryClaimNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = Now;
        var candidates = await (
                from step in _db.StepRuns.AsNoTracking()
                join run in _db.WorkflowRuns.AsNoTracking() on step.WorkflowRunId equals run.Id
                where step.LeaseOwner == null
                      && step.NextAttemptAt <= now
                      && (((step.Status == StepRunStatus.Ready || step.Status == StepRunStatus.Retrying)
                           && run.Status == WorkflowRunStatus.Running)
                          || (step.Status == StepRunStatus.Compensating && run.Status == WorkflowRunStatus.Compensating))
                orderby step.NextAttemptAt
                select new ClaimCandidate(
                    step.Id,
                    step.WorkflowRunId,
                    run.WorkflowDefinitionId,
                    step.StepKey,
                    step.Status,
                    step.Version,
                    step.Attempt,
                    step.CompensationAttempt))
            .Take(_options.ClaimBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            // Defence in depth: skip runs another worker is doing bookkeeping on right now.
            await using var runLock = await _locks.TryAcquireAsync(LockKeys.Run(candidate.RunId), _options.RunLockExpiry, cancellationToken);
            if (runLock is null)
            {
                continue;
            }

            var claimed = await TryClaimAsync(candidate, workerId, now, cancellationToken);
            if (claimed is not null)
            {
                return claimed;
            }
        }

        return null;
    }

    public async Task<StepExecutionOutcome?> ProcessNextAsync(string workerId, CancellationToken cancellationToken)
    {
        var claim = await TryClaimNextAsync(workerId, cancellationToken);
        return claim is null ? null : await ExecuteAsync(claim, cancellationToken);
    }

    public async Task<StepExecutionOutcome> ExecuteAsync(ClaimedStep claim, CancellationToken stoppingToken)
    {
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["RunId"] = claim.RunId,
            ["StepKey"] = claim.StepKey,
            ["Attempt"] = claim.Attempt,
            ["Compensation"] = claim.IsCompensation,
        });

        var run = await _db.WorkflowRuns.AsNoTracking()
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == claim.RunId, stoppingToken);
        var stepRun = run?.Steps.FirstOrDefault(s => s.Id == claim.StepRunId);
        if (run is null || stepRun is null || stepRun.LeaseOwner != claim.WorkerId || stepRun.Version != claim.Version)
        {
            _logger.LogWarning("Claim on step {StepKey} of run {RunId} is no longer valid; skipping", claim.StepKey, claim.RunId);
            return StepExecutionOutcome.LeaseLost;
        }

        var lease = new LeaseTracker(claim.Version);
        if (!claim.IsCompensation && run.Status != WorkflowRunStatus.Running)
        {
            return await RecordAsync(claim, lease, StepExecutionOutcome.Cancelled, (r, d, now) =>
                r.RecordStepCancelled(d, claim.StepKey, $"Run is {run.Status}; step not executed.", now));
        }

        var definition = await _definitions.GetAsync(claim.WorkflowDefinitionId, stoppingToken);
        var stepDefinition = definition.GetStep(claim.StepKey);
        var handlerType = claim.IsCompensation ? stepDefinition.CompensationHandlerType ?? string.Empty : stepDefinition.HandlerType;
        var rawConfig = claim.IsCompensation ? stepDefinition.CompensationConfigJson : stepDefinition.HandlerConfigJson;

        using var abortCts = new CancellationTokenSource();
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, abortCts.Token);
        executionCts.CancelAfter(stepDefinition.Timeout);
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(claim, lease, abortCts, heartbeatCts.Token);

        var stopwatch = Stopwatch.StartNew();
        string? outputJson = null;
        Exception? failure = null;
        try
        {
            var handler = _handlers.Find(handlerType)
                ?? throw new StepFailedException($"No step handler is registered for type '{handlerType}'.", retryable: false);
            var templateContext = new TemplateContext(
                run.InputJson,
                run.Steps.Where(s => s.OutputJson is not null).ToDictionary(s => s.StepKey, s => s.OutputJson, StringComparer.Ordinal));
            var context = new StepExecutionContext
            {
                RunId = run.Id,
                StepKey = claim.StepKey,
                Attempt = claim.Attempt,
                IsCompensation = claim.IsCompensation,
                CorrelationId = run.CorrelationId,
                ConfigJson = _templates.Resolve(rawConfig, templateContext),
                InputJson = run.InputJson,
                StepOutputJson = stepRun.OutputJson,
            };

            var result = await handler.ExecuteAsync(context, executionCts.Token);
            outputJson = result.OutputJson;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
            stopwatch.Stop();
        }

        var durationMs = stopwatch.ElapsedMilliseconds;
        if (lease.AbortReason == LeaseAbortReason.LeaseLost)
        {
            _logger.LogWarning("Lease on step {StepKey} was lost during execution; discarding the attempt's result", claim.StepKey);
            return StepExecutionOutcome.LeaseLost;
        }

        if (failure is null)
        {
            _logger.LogInformation("Step {StepKey} {Phase} succeeded in {DurationMs} ms", claim.StepKey, Phase(claim), durationMs);
            return claim.IsCompensation
                ? await RecordAsync(claim, lease, StepExecutionOutcome.Succeeded, (r, d, now) =>
                    r.RecordCompensationSucceeded(d, claim.StepKey, now))
                : await RecordAsync(claim, lease, StepExecutionOutcome.Succeeded, (r, d, now) =>
                    r.RecordStepSucceeded(d, claim.StepKey, outputJson, durationMs, now));
        }

        if (failure is OperationCanceledException && stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker shutting down; releasing step {StepKey}", claim.StepKey);
            return await RecordAsync(claim, lease, StepExecutionOutcome.Released, (r, d, now) =>
                r.ReleaseStep(d, claim.StepKey, "Worker shut down during the attempt.", now));
        }

        if (failure is OperationCanceledException && lease.AbortReason == LeaseAbortReason.RunStopped)
        {
            _logger.LogInformation("Run is no longer running; step {StepKey} cancelled cooperatively", claim.StepKey);
            return await RecordAsync(claim, lease, StepExecutionOutcome.Cancelled, (r, d, now) =>
                r.RecordStepCancelled(d, claim.StepKey, $"Run became {r.Status} while the step was running.", now));
        }

        var timedOut = failure is OperationCanceledException && executionCts.IsCancellationRequested;
        var error = timedOut
            ? $"Timed out after {stepDefinition.TimeoutSeconds}s."
            : $"{failure.GetType().Name}: {failure.Message}";
        var retryable = failure is not StepFailedException stepFailure || stepFailure.Retryable;
        var policy = _retryPolicies.Create(stepDefinition.Retry);
        _logger.LogWarning(failure, "Step {StepKey} {Phase} attempt {Attempt} failed (retryable: {Retryable})", claim.StepKey, Phase(claim), claim.Attempt, retryable);

        if (claim.IsCompensation)
        {
            var compensationRetryAt = retryable ? policy.GetNextAttemptAt(claim.Attempt) : null;
            return await RecordAsync(
                claim,
                lease,
                compensationRetryAt is null ? StepExecutionOutcome.Failed : StepExecutionOutcome.RetryScheduled,
                (r, d, now) => r.RecordCompensationFailed(d, claim.StepKey, error, compensationRetryAt, now));
        }

        var retryAt = retryable ? policy.GetNextAttemptAt(claim.Attempt) : null;
        return await RecordAsync(
            claim,
            lease,
            retryAt is null ? StepExecutionOutcome.Failed : StepExecutionOutcome.RetryScheduled,
            (r, d, now) => r.RecordStepFailed(d, claim.StepKey, error, retryAt, durationMs, now));
    }

    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var expired = await _db.StepRuns.AsNoTracking()
            .Where(s => s.LeaseOwner != null && s.LeaseExpiresAt < now)
            .OrderBy(s => s.LeaseExpiresAt)
            .Select(s => new { s.Id, s.WorkflowRunId, s.StepKey })
            .Take(_options.RecoveryBatchSize)
            .ToListAsync(cancellationToken);

        var recovered = 0;
        foreach (var item in expired)
        {
            try
            {
                var saved = await _mutator.MutateAsync(item.WorkflowRunId, (run, definition, at) =>
                {
                    var step = run.Steps.FirstOrDefault(s => s.Id == item.Id);
                    if (step?.LeaseExpiresAt is not DateTime expiresAt || expiresAt >= at || step.LeaseOwner is null)
                    {
                        return false; // heartbeat extended it or someone already settled it
                    }

                    var policy = _retryPolicies.Create(definition.GetStep(step.StepKey).Retry);
                    var attemptsMade = step.Status == StepRunStatus.Compensating ? step.CompensationAttempt : step.Attempt;
                    _logger.LogWarning("Recovering expired lease on step {StepKey} of run {RunId} (owner {Owner})", step.StepKey, run.Id, step.LeaseOwner);
                    return run.RecoverExpiredLease(definition, step.StepKey, policy.GetNextAttemptAt(attemptsMade), at);
                }, cancellationToken);

                if (saved is not null)
                {
                    recovered++;
                }
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogDebug(ex, "Lease recovery for step {StepKey} lost a race; it will be re-evaluated", item.StepKey);
            }
        }

        return recovered;
    }

    private async Task<ClaimedStep?> TryClaimAsync(ClaimCandidate candidate, string workerId, DateTime now, CancellationToken cancellationToken)
    {
        DateTime? leaseUntil = now + _options.LeaseDuration;
        DateTime? startedAt = now;
        var newVersion = Guid.NewGuid();
        var isCompensation = candidate.Status == StepRunStatus.Compensating;
        int affected;

        // Compare-and-swap on (Id, Version, Status, no lease): exactly one worker can win.
        if (isCompensation)
        {
            affected = await _db.StepRuns
                .Where(s => s.Id == candidate.StepRunId
                            && s.Version == candidate.Version
                            && s.Status == StepRunStatus.Compensating
                            && s.LeaseOwner == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(s => s.LeaseOwner, workerId)
                        .SetProperty(s => s.LeaseExpiresAt, leaseUntil)
                        .SetProperty(s => s.CompensationAttempt, s => s.CompensationAttempt + 1)
                        .SetProperty(s => s.Version, newVersion),
                    cancellationToken);
        }
        else
        {
            var expectedStatus = candidate.Status;
            affected = await _db.StepRuns
                .Where(s => s.Id == candidate.StepRunId
                            && s.Version == candidate.Version
                            && s.Status == expectedStatus
                            && s.LeaseOwner == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(s => s.Status, StepRunStatus.Running)
                        .SetProperty(s => s.LeaseOwner, workerId)
                        .SetProperty(s => s.LeaseExpiresAt, leaseUntil)
                        .SetProperty(s => s.Attempt, s => s.Attempt + 1)
                        .SetProperty(s => s.StartedAt, s => s.StartedAt ?? startedAt)
                        .SetProperty(s => s.Version, newVersion),
                    cancellationToken);
        }

        if (affected != 1)
        {
            return null;
        }

        var attempt = isCompensation ? candidate.CompensationAttempt + 1 : candidate.Attempt + 1;
        _db.ExecutionEvents.Add(ExecutionEvent.Create(
            candidate.RunId,
            isCompensation ? ExecutionEventType.StepCompensationStarted : ExecutionEventType.StepStarted,
            isCompensation
                ? $"Compensation of '{candidate.StepKey}' started (attempt {attempt})."
                : $"Step '{candidate.StepKey}' started (attempt {attempt}).",
            now,
            candidate.StepKey,
            JsonText.Serialize(new { attempt, workerId })));
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Worker {WorkerId} claimed step {StepKey} of run {RunId} ({Phase}, attempt {Attempt})",
            workerId,
            candidate.StepKey,
            candidate.RunId,
            isCompensation ? "compensation" : "execution",
            attempt);

        return new ClaimedStep(
            candidate.StepRunId,
            candidate.RunId,
            candidate.WorkflowDefinitionId,
            candidate.StepKey,
            isCompensation,
            attempt,
            workerId,
            newVersion,
            leaseUntil.Value);
    }

    private async Task HeartbeatAsync(ClaimedStep claim, LeaseTracker lease, CancellationTokenSource abortCts, CancellationToken stopToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(stopToken))
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IOrchestratorDbContext>();
                DateTime? leaseUntil = Now + _options.LeaseDuration;
                var currentVersion = lease.Version;
                var newVersion = Guid.NewGuid();
                var owner = claim.WorkerId;

                // Not cancellable on purpose: if this UPDATE commits we must learn the new fencing token.
                var affected = await db.StepRuns
                    .Where(s => s.Id == claim.StepRunId && s.LeaseOwner == owner && s.Version == currentVersion)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(s => s.LeaseExpiresAt, leaseUntil)
                            .SetProperty(s => s.Version, newVersion),
                        CancellationToken.None);
                if (affected != 1)
                {
                    lease.Abort(LeaseAbortReason.LeaseLost);
                    abortCts.Cancel();
                    return;
                }

                lease.Version = newVersion;
                if (!claim.IsCompensation)
                {
                    var runStatus = await db.WorkflowRuns.AsNoTracking()
                        .Where(r => r.Id == claim.RunId)
                        .Select(r => r.Status)
                        .FirstAsync(stopToken);
                    if (runStatus != WorkflowRunStatus.Running)
                    {
                        lease.Abort(LeaseAbortReason.RunStopped);
                        abortCts.Cancel();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // Attempt finished; heartbeat no longer needed.
        }
        catch (Exception ex)
        {
            // A failed heartbeat is not fatal: if the lease really expires, recovery re-queues the step.
            _logger.LogWarning(ex, "Heartbeat for step {StepKey} of run {RunId} failed", claim.StepKey, claim.RunId);
        }
    }

    private async Task<StepExecutionOutcome> RecordAsync(
        ClaimedStep claim,
        LeaseTracker lease,
        StepExecutionOutcome outcome,
        Action<WorkflowRun, WorkflowDefinition, DateTime> apply)
    {
        // Recording must survive host shutdown, so it uses its own bounded token.
        using var cts = new CancellationTokenSource(RecordingTimeout);
        var saved = await _mutator.MutateAsync(claim.RunId, (run, definition, now) =>
        {
            var step = run.Steps.FirstOrDefault(s => s.Id == claim.StepRunId);
            if (step is null || step.LeaseOwner != claim.WorkerId || step.Version != lease.Version)
            {
                return false; // fenced out: the lease was recovered and possibly re-claimed
            }

            apply(run, definition, now);
            return true;
        }, cts.Token);

        if (saved is null)
        {
            _logger.LogWarning("Result of step {StepKey} of run {RunId} was not recorded (lease lost)", claim.StepKey, claim.RunId);
            return StepExecutionOutcome.LeaseLost;
        }

        return outcome;
    }

    private static string Phase(ClaimedStep claim) => claim.IsCompensation ? "compensation" : "execution";

    private sealed record ClaimCandidate(
        Guid StepRunId,
        Guid RunId,
        Guid WorkflowDefinitionId,
        string StepKey,
        StepRunStatus Status,
        Guid Version,
        int Attempt,
        int CompensationAttempt);
}

internal enum LeaseAbortReason
{
    None = 0,
    LeaseLost = 1,
    RunStopped = 2,
}

/// <summary>Shared state between an executing attempt and its heartbeat loop.</summary>
internal sealed class LeaseTracker
{
    private int _abortReason;

    public LeaseTracker(Guid version)
    {
        Version = version;
    }

    /// <summary>Current fencing token; replaced by every successful heartbeat.</summary>
    public Guid Version { get; set; }

    public LeaseAbortReason AbortReason => (LeaseAbortReason)Volatile.Read(ref _abortReason);

    public void Abort(LeaseAbortReason reason) => Interlocked.CompareExchange(ref _abortReason, (int)reason, 0);
}
