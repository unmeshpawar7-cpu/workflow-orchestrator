using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestrator.Application;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Engine;
using Orchestrator.Application.Services;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Workflows;
using Orchestrator.Infrastructure;
using Orchestrator.Infrastructure.Coordination;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.UnitTests.TestSupport;

/// <summary>Deterministic clock for tests.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public static ManualTimeProvider Default() => new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));

    public DateTime UtcNow => GetUtcNow().UtcDateTime;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }
}

public static class Specs
{
    public static readonly DateTime Now = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    public static StepDefinitionSpec Step(
        string key,
        string handler = "log",
        string[]? dependsOn = null,
        string? config = null,
        int maxAttempts = 3,
        int initialDelayMs = 1000,
        int timeoutSeconds = 30,
        string? compensation = null,
        string? compensationConfig = null) =>
        new(
            key,
            handler,
            config,
            dependsOn ?? Array.Empty<string>(),
            timeoutSeconds,
            new RetrySettings(maxAttempts, TimeSpan.FromMilliseconds(initialDelayMs), 2.0, TimeSpan.FromMinutes(1), 0.2),
            compensation,
            compensationConfig);

    public static WorkflowDefinition Definition(params StepDefinitionSpec[] steps) =>
        WorkflowDefinition.Create("test-workflow", 1, null, 10, steps, Now);

    /// <summary>A -> B -> C linear chain where every step has a compensation handler.</summary>
    public static WorkflowDefinition LinearWithCompensation() =>
        Definition(
            Step("a", compensation: "log"),
            Step("b", dependsOn: new[] { "a" }, compensation: "log"),
            Step("c", dependsOn: new[] { "b" }));
}

/// <summary>Records every invocation; used to assert execution and compensation order.</summary>
public sealed class RecordingLog
{
    private readonly ConcurrentQueue<(string StepKey, bool Compensation, int Attempt)> _calls = new();

    public IReadOnlyList<(string StepKey, bool Compensation, int Attempt)> Calls => _calls.ToList();

    public void Add(string stepKey, bool compensation, int attempt) => _calls.Enqueue((stepKey, compensation, attempt));
}

public sealed class RecordingStepHandler : IStepHandler
{
    private readonly RecordingLog _log;

    public RecordingStepHandler(RecordingLog log)
    {
        _log = log;
    }

    public string Type => "record";

    public Task<StepResult> ExecuteAsync(StepExecutionContext context, CancellationToken cancellationToken)
    {
        _log.Add(context.StepKey, context.IsCompensation, context.Attempt);
        return Task.FromResult(StepResult.FromObject(new { step = context.StepKey, attempt = context.Attempt }));
    }
}

/// <summary>
/// Real engine (application + infrastructure services) over a temporary SQLite file database, a manual clock and
/// in-memory coordination. Tests drive the coordinator tick and worker process methods directly, so nothing
/// depends on background timing.
/// </summary>
public sealed class EngineHarness : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orchestrator-unit-{Guid.NewGuid():N}.db");

    public EngineHarness(Action<WorkerOptions>? configureWorker = null, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(Clock);

        // Jitter sample 0.5 => factor 1.0: retry delays are exactly the exponential base delay.
        services.AddSingleton<IRetryPolicyFactory>(new RetryPolicyFactory(Clock, () => 0.5));
        services.AddOrchestratorApplication();
        services.AddDbContext<OrchestratorDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddScoped<IOrchestratorDbContext>(sp => sp.GetRequiredService<OrchestratorDbContext>());
        services.AddSingleton<InMemoryDistributedLockProvider>();
        services.AddSingleton<IDistributedLockProvider>(sp => sp.GetRequiredService<InMemoryDistributedLockProvider>());
        services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.AddStepHandlers();
        services.AddSingleton(Recording);
        services.AddTransient<IStepHandler, RecordingStepHandler>();
        services.Configure<WorkerOptions>(options =>
        {
            options.LeaseDuration = TimeSpan.FromSeconds(30);
            options.HeartbeatInterval = TimeSpan.FromHours(1); // no heartbeats unless a test opts in
            options.RunLockWaitTimeout = TimeSpan.FromSeconds(2);
            configureWorker?.Invoke(options);
        });
        configureServices?.Invoke(services);

        Services = services.BuildServiceProvider();
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreated();
    }

    public ManualTimeProvider Clock { get; } = ManualTimeProvider.Default();

    public RecordingLog Recording { get; } = new();

    public ServiceProvider Services { get; }

    public const string WorkerId = "test-worker";

    public async Task<WorkflowDefinition> SaveDefinitionAsync(WorkflowDefinition definition)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        return definition;
    }

    public async Task<Guid> CreateRunAsync(WorkflowDefinition definition, string inputJson = "{}", string? idempotencyKey = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunService>();
        var request = new CreateRunRequest { Input = System.Text.Json.JsonDocument.Parse(inputJson).RootElement.Clone() };
        var result = await runs.CreateAsync(definition.Id, request, idempotencyKey, "test-correlation", CancellationToken.None);
        return result.Run.Id;
    }

    public async Task<CoordinatorTickResult> TickAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowCoordinator>().TickAsync(CancellationToken.None);
    }

    public async Task<StepExecutionOutcome?> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStepExecutor>().ProcessNextAsync(WorkerId, cancellationToken);
    }

    public async Task<ClaimedStep?> ClaimAsync(string workerId = WorkerId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStepExecutor>().TryClaimNextAsync(workerId, CancellationToken.None);
    }

    public async Task<StepExecutionOutcome> ExecuteAsync(ClaimedStep claim)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStepExecutor>().ExecuteAsync(claim, CancellationToken.None);
    }

    public async Task<int> RecoverAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStepExecutor>().RecoverExpiredLeasesAsync(CancellationToken.None);
    }

    public async Task<WorkflowRun> GetRunAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().WorkflowRuns
            .AsNoTracking()
            .Include(r => r.Steps)
            .SingleAsync(r => r.Id == runId);
    }

    public async Task<IReadOnlyList<ExecutionEvent>> GetEventsAsync(Guid runId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().ExecutionEvents
            .AsNoTracking()
            .Where(e => e.WorkflowRunId == runId)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Alternates coordinator ticks and worker processing; when nothing is due it advances the clock so retry
    /// back-offs elapse. Stops when the run reaches a resting state.
    /// </summary>
    public async Task<WorkflowRun> DriveAsync(Guid runId, int maxIterations = 50)
    {
        for (var i = 0; i < maxIterations; i++)
        {
            await TickAsync();
            var processed = false;
            while (await ProcessNextAsync() is not null)
            {
                processed = true;
            }

            var run = await GetRunAsync(runId);
            if (IsResting(run))
            {
                return run;
            }

            if (!processed)
            {
                Clock.Advance(TimeSpan.FromSeconds(5));
            }
        }

        throw new TimeoutException($"Run {runId} did not settle within {maxIterations} iterations.");
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // best effort cleanup of a temp file
            }
        }
    }

    private static bool IsResting(WorkflowRun run) =>
        run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Compensated or WorkflowRunStatus.Cancelled or WorkflowRunStatus.DeadLettered
        || (run.Status == WorkflowRunStatus.Failed && run.Steps.All(s => s.Status != StepRunStatus.Running));
}
