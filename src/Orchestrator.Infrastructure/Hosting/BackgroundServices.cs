using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Engine;

namespace Orchestrator.Infrastructure.Hosting;

/// <summary>
/// Run coordinator loop: every <c>Scheduler:PollInterval</c> fires due schedules, starts due runs and reconciles
/// idle runs. Stateless between ticks, so any number of instances may run (they coordinate through the DB).
/// </summary>
public sealed class WorkflowSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SchedulerOptions _options;
    private readonly ILogger<WorkflowSchedulerService> _logger;

    public WorkflowSchedulerService(
        IServiceScopeFactory scopeFactory,
        IOptions<SchedulerOptions> options,
        ILogger<WorkflowSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Workflow scheduler disabled (Scheduler:Enabled=false)");
            return;
        }

        _logger.LogInformation("Workflow scheduler started (poll interval {PollInterval})", _options.PollInterval);
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            do
            {
                await TickAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }

        _logger.LogInformation("Workflow scheduler stopped");
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<IWorkflowCoordinator>();
            var result = await coordinator.TickAsync(stoppingToken);
            if (result.SchedulesFired + result.RunsStarted + result.RunsReconciled > 0)
            {
                _logger.LogDebug(
                    "Scheduler tick: {Fired} schedule(s) fired, {Started} run(s) started, {Reconciled} run(s) reconciled",
                    result.SchedulesFired,
                    result.RunsStarted,
                    result.RunsReconciled);
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Scheduler tick failed; retrying on the next tick");
        }
    }
}

/// <summary>
/// Step worker loop. Lives in the API process today but depends only on the engine abstractions, so it can be
/// moved to a dedicated worker host unchanged (see docs/architecture.md, "Splitting the worker").
/// <list type="bullet">
/// <item>Recovers expired leases, then claims steps while a <see cref="SemaphoreSlim"/> slot is free
/// (<c>Worker:MaxDegreeOfParallelism</c>).</item>
/// <item>Each claimed step executes in its own DI scope (own DbContext) on the thread pool.</item>
/// <item>On shutdown, in-flight steps observe the stopping token, hand their step back and finish recording.</item>
/// </list>
/// </summary>
public sealed class StepWorkerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkerOptions _options;
    private readonly ILogger<StepWorkerService> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    public StepWorkerService(IServiceScopeFactory scopeFactory, IOptions<WorkerOptions> options, ILogger<StepWorkerService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        WorkerId = BuildWorkerId(_options.WorkerId);
    }

    public string WorkerId { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Step worker disabled (Worker:Enabled=false)");
            return;
        }

        _logger.LogInformation(
            "Step worker {WorkerId} started (parallelism {Parallelism}, lease {Lease}, heartbeat {Heartbeat})",
            WorkerId,
            _options.MaxDegreeOfParallelism,
            _options.LeaseDuration,
            _options.HeartbeatInterval);

        using var slots = new SemaphoreSlim(_options.MaxDegreeOfParallelism, _options.MaxDegreeOfParallelism);
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            do
            {
                PruneCompleted();
                await RecoverLeasesAsync(stoppingToken);
                await FillSlotsAsync(slots, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            // Executions never throw out of RunClaimAsync, so this only waits for them to settle.
            await Task.WhenAll(_inFlight.Values);
            _logger.LogInformation("Step worker {WorkerId} stopped", WorkerId);
        }
    }

    private async Task RecoverLeasesAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<IStepExecutor>();
            var recovered = await executor.RecoverExpiredLeasesAsync(stoppingToken);
            if (recovered > 0)
            {
                _logger.LogWarning("Recovered {Count} step(s) with expired leases", recovered);
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Lease recovery failed");
        }
    }

    private async Task FillSlotsAsync(SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && await slots.WaitAsync(0, stoppingToken))
        {
            ClaimedStep? claim = null;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var executor = scope.ServiceProvider.GetRequiredService<IStepExecutor>();
                claim = await executor.TryClaimNextAsync(WorkerId, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Claiming a step failed");
            }
            finally
            {
                if (claim is null)
                {
                    slots.Release();
                }
            }

            if (claim is null)
            {
                return; // nothing due (or claim failed); wait for the next tick
            }

            var executionId = Guid.NewGuid();
            var claimed = claim;
            var execution = Task.Run(() => RunClaimAsync(claimed, slots, stoppingToken), CancellationToken.None);
            _inFlight[executionId] = execution;
        }
    }

    private async Task RunClaimAsync(ClaimedStep claim, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<IStepExecutor>();
            var outcome = await executor.ExecuteAsync(claim, stoppingToken);
            _logger.LogDebug("Step {StepKey} of run {RunId} finished: {Outcome}", claim.StepKey, claim.RunId, outcome);
        }
        catch (Exception ex)
        {
            // The lease will expire and recovery will re-queue the step.
            _logger.LogError(ex, "Executing step {StepKey} of run {RunId} failed unexpectedly", claim.StepKey, claim.RunId);
        }
        finally
        {
            slots.Release();
        }
    }

    private void PruneCompleted()
    {
        foreach (var entry in _inFlight)
        {
            if (entry.Value.IsCompleted)
            {
                _inFlight.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string BuildWorkerId(string? configured)
    {
        var id = string.IsNullOrWhiteSpace(configured)
            ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"
            : configured.Trim();
        return id.Length <= 200 ? id : id[..200];
    }
}
