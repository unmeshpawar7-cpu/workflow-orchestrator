using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Common;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Application.Engine;

/// <summary>Creates <see cref="RetryPolicy"/> instances; tests replace the random source to make jitter deterministic.</summary>
public interface IRetryPolicyFactory
{
    RetryPolicy Create(RetrySettings settings);
}

public sealed class RetryPolicyFactory : IRetryPolicyFactory
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<double>? _randomSource;

    public RetryPolicyFactory(TimeProvider timeProvider, Func<double>? randomSource = null)
    {
        _timeProvider = timeProvider;
        _randomSource = randomSource;
    }

    public RetryPolicy Create(RetrySettings settings) => new(settings, _timeProvider, _randomSource);
}

/// <summary>
/// Process-wide cache of workflow definitions. Safe because definitions are immutable once created.
/// </summary>
public sealed class DefinitionCache
{
    private const int MaxEntries = 1000;
    private readonly ConcurrentDictionary<Guid, WorkflowDefinition> _entries = new();

    public bool TryGet(Guid id, out WorkflowDefinition? definition)
    {
        var found = _entries.TryGetValue(id, out var cached);
        definition = cached;
        return found;
    }

    public void Add(WorkflowDefinition definition)
    {
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        _entries[definition.Id] = definition;
    }
}

public interface IDefinitionProvider
{
    Task<WorkflowDefinition?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<WorkflowDefinition> GetAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class DefinitionProvider : IDefinitionProvider
{
    private readonly IOrchestratorDbContext _db;
    private readonly DefinitionCache _cache;

    public DefinitionProvider(IOrchestratorDbContext db, DefinitionCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<WorkflowDefinition?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_cache.TryGet(id, out var cached) && cached is not null)
        {
            return cached;
        }

        var definition = await _db.WorkflowDefinitions
            .AsNoTracking()
            .Include(d => d.Steps)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (definition is not null)
        {
            _cache.Add(definition);
        }

        return definition;
    }

    public async Task<WorkflowDefinition> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await FindAsync(id, cancellationToken) ?? throw NotFoundException.For("Workflow definition", id);
}

/// <summary>
/// Loads a run aggregate, applies a mutation and saves it, retrying on optimistic-concurrency conflicts.
/// The per-run distributed lock is taken first as defence in depth (fewer conflicts); if it cannot be
/// obtained in time the mutation proceeds anyway, because the row versions guarantee correctness.
/// </summary>
public sealed class RunMutator
{
    private const int MaxAttempts = 5;

    private readonly IOrchestratorDbContext _db;
    private readonly IDefinitionProvider _definitions;
    private readonly IDistributedLockProvider _locks;
    private readonly TimeProvider _timeProvider;
    private readonly WorkerOptions _options;
    private readonly ILogger<RunMutator> _logger;

    public RunMutator(
        IOrchestratorDbContext db,
        IDefinitionProvider definitions,
        IDistributedLockProvider locks,
        TimeProvider timeProvider,
        IOptions<WorkerOptions> options,
        ILogger<RunMutator> logger)
    {
        _db = db;
        _definitions = definitions;
        _locks = locks;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <param name="runId">Identifier of the run to load, lock and update.</param>
    /// <param name="mutate">Applies the change; return <c>false</c> to abort without saving.</param>
    /// <param name="cancellationToken">Cancels waiting for the run lock and the database work.</param>
    /// <returns>The saved run, or <c>null</c> when the run does not exist or the mutation aborted.</returns>
    public async Task<WorkflowRun?> MutateAsync(
        Guid runId,
        Func<WorkflowRun, WorkflowDefinition, DateTime, bool> mutate,
        CancellationToken cancellationToken)
    {
        await using var runLock = await _locks.AcquireAsync(
            LockKeys.Run(runId),
            _options.RunLockExpiry,
            _options.RunLockWaitTimeout,
            cancellationToken);
        if (runLock is null)
        {
            _logger.LogWarning("Run lock for {RunId} not acquired in time; relying on optimistic concurrency", runId);
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            _db.ChangeTracker.Clear();
            var run = await _db.WorkflowRuns
                .Include(r => r.Steps)
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
            if (run is null)
            {
                return null;
            }

            var definition = await _definitions.GetAsync(run.WorkflowDefinitionId, cancellationToken);
            if (!mutate(run, definition, _timeProvider.GetUtcNow().UtcDateTime))
            {
                _db.ChangeTracker.Clear();
                return null;
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return run;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                _logger.LogDebug("Concurrency conflict on run {RunId} (attempt {Attempt}); reloading", runId, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 25) * attempt), cancellationToken);
            }
        }

        return null;
    }
}
