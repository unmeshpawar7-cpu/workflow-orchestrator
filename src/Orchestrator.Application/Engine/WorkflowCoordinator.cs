using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Common;
using Orchestrator.Domain.Runs;

namespace Orchestrator.Application.Engine;

public sealed record CoordinatorTickResult(int SchedulesFired, int RunsStarted, int RunsReconciled);

public interface IWorkflowCoordinator
{
    /// <summary>
    /// One scheduler pass: fire due schedules, start due Pending runs (respecting MaxConcurrentRuns) and
    /// reconcile idle Running/Compensating runs. Safe to run concurrently on several instances.
    /// </summary>
    Task<CoordinatorTickResult> TickAsync(CancellationToken cancellationToken);
}

public sealed class WorkflowCoordinator : IWorkflowCoordinator
{
    private readonly IOrchestratorDbContext _db;
    private readonly IDefinitionProvider _definitions;
    private readonly RunMutator _mutator;
    private readonly TimeProvider _timeProvider;
    private readonly SchedulerOptions _options;
    private readonly ILogger<WorkflowCoordinator> _logger;

    public WorkflowCoordinator(
        IOrchestratorDbContext db,
        IDefinitionProvider definitions,
        RunMutator mutator,
        TimeProvider timeProvider,
        IOptions<SchedulerOptions> options,
        ILogger<WorkflowCoordinator> logger)
    {
        _db = db;
        _definitions = definitions;
        _mutator = mutator;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<CoordinatorTickResult> TickAsync(CancellationToken cancellationToken)
    {
        var fired = await FireDueSchedulesAsync(cancellationToken);
        var started = await StartDueRunsAsync(cancellationToken);
        var reconciled = await ReconcileIdleRunsAsync(cancellationToken);
        return new CoordinatorTickResult(fired, started, reconciled);
    }

    private async Task<int> FireDueSchedulesAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var dueIds = await _db.Schedules.AsNoTracking()
            .Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= now)
            .OrderBy(s => s.NextRunAt)
            .Select(s => s.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var fired = 0;
        foreach (var scheduleId in dueIds)
        {
            _db.ChangeTracker.Clear();
            try
            {
                var schedule = await _db.Schedules.FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
                if (schedule is null || !schedule.IsDue(now))
                {
                    continue;
                }

                var definition = await _definitions.GetAsync(schedule.WorkflowDefinitionId, cancellationToken);
                var fireTime = schedule.Fire(now);

                // Deterministic key: two scheduler instances firing the same slot collide on the unique index.
                var idempotencyKey = $"schedule:{schedule.Id:N}:{fireTime:yyyyMMddTHHmmssfff}";
                var run = WorkflowRun.Create(
                    definition,
                    schedule.InputJson,
                    correlationId: $"schedule-{schedule.Id:N}",
                    scheduledFor: fireTime,
                    now: now,
                    idempotencyKey: idempotencyKey,
                    requestHash: RequestHasher.Hash(definition.Id.ToString(), RequestHasher.Canonicalize(schedule.InputJson), idempotencyKey),
                    scheduleId: schedule.Id);
                _db.WorkflowRuns.Add(run);

                // Schedule update + run + steps + events commit atomically in one SaveChanges transaction.
                await _db.SaveChangesAsync(cancellationToken);
                fired++;
                _logger.LogInformation("Schedule {ScheduleId} fired run {RunId} for {FireTime:O}", schedule.Id, run.Id, fireTime);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogInformation(ex, "Schedule {ScheduleId} was fired concurrently by another instance", scheduleId);
            }
        }

        _db.ChangeTracker.Clear();
        return fired;
    }

    private async Task<int> StartDueRunsAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var due = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.Status == WorkflowRunStatus.Pending && r.ScheduledFor <= now)
            .OrderBy(r => r.ScheduledFor)
            .Select(r => new { r.Id, r.WorkflowDefinitionId })
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var started = 0;
        foreach (var item in due)
        {
            var definition = await _definitions.GetAsync(item.WorkflowDefinitionId, cancellationToken);
            var active = await _db.WorkflowRuns.CountAsync(
                r => r.WorkflowDefinitionId == item.WorkflowDefinitionId
                     && (r.Status == WorkflowRunStatus.Running || r.Status == WorkflowRunStatus.Compensating),
                cancellationToken);
            if (active >= definition.MaxConcurrentRuns)
            {
                continue; // stays Pending until a slot frees up
            }

            try
            {
                var saved = await _mutator.MutateAsync(item.Id, (run, def, at) =>
                {
                    if (run.Status != WorkflowRunStatus.Pending)
                    {
                        return false;
                    }

                    run.Start(def, at);
                    return true;
                }, cancellationToken);
                if (saved is not null)
                {
                    started++;
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogDebug("Run {RunId} was started concurrently", item.Id);
            }
        }

        return started;
    }

    private async Task<int> ReconcileIdleRunsAsync(CancellationToken cancellationToken)
    {
        // "Idle" = active run with no step that a worker can act on. Such a run is either finished, blocked on a
        // dependency promotion that was missed, or waiting for the next compensation to be scheduled.
        var idle = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => (r.Status == WorkflowRunStatus.Running || r.Status == WorkflowRunStatus.Compensating)
                        && !r.Steps.Any(s => s.Status == StepRunStatus.Ready
                                             || s.Status == StepRunStatus.Running
                                             || s.Status == StepRunStatus.Retrying
                                             || s.Status == StepRunStatus.Compensating))
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var reconciled = 0;
        foreach (var runId in idle)
        {
            try
            {
                var saved = await _mutator.MutateAsync(runId, (run, definition, at) =>
                {
                    var before = run.Version;
                    run.Reconcile(definition, at);
                    return run.Version != before;
                }, cancellationToken);
                if (saved is not null)
                {
                    reconciled++;
                    _logger.LogInformation("Reconciled idle run {RunId} -> {Status}", runId, saved.Status);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogDebug("Run {RunId} changed while reconciling; will retry next tick", runId);
            }
        }

        return reconciled;
    }
}
