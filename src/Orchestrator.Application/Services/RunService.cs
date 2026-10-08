using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Common;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Engine;
using Orchestrator.Application.Validation;
using Orchestrator.Domain.Common;
using Orchestrator.Domain.Runs;

namespace Orchestrator.Application.Services;

public interface IRunService
{
    Task<RunCreationResult> CreateAsync(Guid workflowDefinitionId, CreateRunRequest request, string? idempotencyKey, string correlationId, CancellationToken cancellationToken);

    Task<RunDetailDto> GetAsync(Guid runId, CancellationToken cancellationToken);

    Task<PagedResult<RunSummaryDto>> ListAsync(RunFilter filter, PageRequest page, CancellationToken cancellationToken);

    Task<PagedResult<ExecutionEventDto>> GetHistoryAsync(Guid runId, PageRequest page, CancellationToken cancellationToken);

    Task<RunDetailDto> CancelAsync(Guid runId, CancellationToken cancellationToken);

    Task<RunDetailDto> RetryAsync(Guid runId, CancellationToken cancellationToken);

    Task<RunDetailDto> RequeueDeadLetterAsync(Guid runId, CancellationToken cancellationToken);
}

public sealed class RunService : IRunService
{
    private readonly IOrchestratorDbContext _db;
    private readonly IDefinitionProvider _definitions;
    private readonly IIdempotencyStore _idempotency;
    private readonly IValidator<CreateRunRequest> _validator;
    private readonly RunMutator _mutator;
    private readonly TimeProvider _timeProvider;
    private readonly EngineOptions _options;
    private readonly ILogger<RunService> _logger;

    public RunService(
        IOrchestratorDbContext db,
        IDefinitionProvider definitions,
        IIdempotencyStore idempotency,
        IValidator<CreateRunRequest> validator,
        RunMutator mutator,
        TimeProvider timeProvider,
        IOptions<EngineOptions> options,
        ILogger<RunService> logger)
    {
        _db = db;
        _definitions = definitions;
        _idempotency = idempotency;
        _validator = validator;
        _mutator = mutator;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RunCreationResult> CreateAsync(
        Guid workflowDefinitionId,
        CreateRunRequest request,
        string? idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await _validator.EnsureValidAsync(request, cancellationToken);
        if (idempotencyKey is { Length: > WorkflowRun.MaxIdempotencyKeyLength } || (idempotencyKey is not null && string.IsNullOrWhiteSpace(idempotencyKey)))
        {
            throw new RequestValidationException("Validation failed", new[]
            {
                new ValidationFailure("Idempotency-Key", $"Idempotency-Key must be 1-{WorkflowRun.MaxIdempotencyKeyLength} non-blank characters."),
            });
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var requestedSchedule = UtcTime.Normalize(request.ScheduledFor);
        if (requestedSchedule > now + _options.MaxScheduleAhead)
        {
            throw new RequestValidationException("Validation failed", new[]
            {
                new ValidationFailure("scheduledFor", $"scheduledFor may be at most {_options.MaxScheduleAhead.TotalDays:0} days in the future."),
            });
        }

        var definition = await _definitions.GetAsync(workflowDefinitionId, cancellationToken);
        var inputJson = JsonText.FromElement(request.Input, "{}")!;
        var requestHash = RequestHasher.Hash(
            definition.Id.ToString(),
            RequestHasher.Canonicalize(inputJson),
            requestedSchedule?.ToString("O"));

        if (idempotencyKey is not null)
        {
            var replay = await FindIdempotentRunAsync(definition.Id, idempotencyKey, requestHash, cancellationToken);
            if (replay is not null)
            {
                return new RunCreationResult(replay, Created: false);
            }
        }

        var run = WorkflowRun.Create(
            definition,
            inputJson,
            correlationId,
            requestedSchedule ?? now,
            now,
            idempotencyKey,
            requestHash);
        _db.WorkflowRuns.Add(run);

        try
        {
            // Run row, step rows and the RunCreated event commit together or not at all.
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // Lost a race with a concurrent request using the same key: the unique index decided.
            _db.ChangeTracker.Clear();
            var replay = await FindIdempotentRunAsync(definition.Id, idempotencyKey, requestHash, cancellationToken);
            if (replay is not null)
            {
                return new RunCreationResult(replay, Created: false);
            }

            throw;
        }

        if (idempotencyKey is not null)
        {
            await TryCacheAsync(definition.Id, idempotencyKey, new IdempotencyRecord(run.Id, requestHash), cancellationToken);
        }

        _logger.LogInformation("Created run {RunId} of workflow {DefinitionId} (correlation {CorrelationId})", run.Id, definition.Id, correlationId);
        return new RunCreationResult(await GetAsync(run.Id, cancellationToken), Created: true);
    }

    public async Task<RunDetailDto> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await _db.WorkflowRuns.AsNoTracking()
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw NotFoundException.For("Run", runId);
        return MapDetail(run);
    }

    public async Task<PagedResult<RunSummaryDto>> ListAsync(RunFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        var query = _db.WorkflowRuns.AsNoTracking();
        if (filter.Status is WorkflowRunStatus status)
        {
            query = query.Where(r => r.Status == status);
        }

        if (filter.WorkflowDefinitionId is Guid definitionId)
        {
            query = query.Where(r => r.WorkflowDefinitionId == definitionId);
        }

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Select(r => new RunSummaryDto(
                r.Id,
                r.WorkflowDefinitionId,
                r.Status,
                r.CorrelationId,
                r.IdempotencyKey,
                r.ScheduledFor,
                r.CreatedAt,
                r.StartedAt,
                r.CompletedAt,
                r.Error))
            .ToPagedResultAsync(page, cancellationToken);
    }

    public async Task<PagedResult<ExecutionEventDto>> GetHistoryAsync(Guid runId, PageRequest page, CancellationToken cancellationToken)
    {
        if (!await _db.WorkflowRuns.AnyAsync(r => r.Id == runId, cancellationToken))
        {
            throw NotFoundException.For("Run", runId);
        }

        var raw = await _db.ExecutionEvents.AsNoTracking()
            .Where(e => e.WorkflowRunId == runId)
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.StepKey, e.EventType, e.Message, e.DataJson, e.OccurredAt })
            .ToPagedResultAsync(page, cancellationToken);

        return raw.Map(e => new ExecutionEventDto(e.Id, e.StepKey, e.EventType, e.Message, JsonText.ToElement(e.DataJson), e.OccurredAt));
    }

    public Task<RunDetailDto> CancelAsync(Guid runId, CancellationToken cancellationToken) =>
        MutateAsync(runId, (run, _, now) => run.Cancel(now), cancellationToken);

    public Task<RunDetailDto> RetryAsync(Guid runId, CancellationToken cancellationToken) =>
        MutateAsync(runId, (run, definition, now) => run.Retry(definition, now), cancellationToken);

    public Task<RunDetailDto> RequeueDeadLetterAsync(Guid runId, CancellationToken cancellationToken) =>
        MutateAsync(runId, (run, definition, now) => run.RequeueCompensation(definition, now), cancellationToken);

    internal static RunDetailDto MapDetail(WorkflowRun run) =>
        new(
            run.Id,
            run.WorkflowDefinitionId,
            run.ScheduleId,
            run.Status,
            run.CorrelationId,
            run.IdempotencyKey,
            JsonText.ToElement(run.InputJson),
            JsonText.ToElement(run.OutputJson),
            run.Error,
            run.ScheduledFor,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt,
            run.Steps
                .OrderBy(s => s.Ordinal)
                .Select(s => new StepRunDto(
                    s.StepKey,
                    s.Ordinal,
                    s.Status,
                    s.Attempt,
                    s.CompensationAttempt,
                    s.NextAttemptAt,
                    s.LeaseOwner,
                    s.LeaseExpiresAt,
                    JsonText.ToElement(s.OutputJson),
                    s.LastError,
                    s.StartedAt,
                    s.CompletedAt,
                    s.DurationMs))
                .ToList());

    private async Task<RunDetailDto> MutateAsync(
        Guid runId,
        Action<WorkflowRun, Domain.Workflows.WorkflowDefinition, DateTime> command,
        CancellationToken cancellationToken)
    {
        try
        {
            var saved = await _mutator.MutateAsync(runId, (run, definition, now) =>
            {
                command(run, definition, now);
                return true;
            }, cancellationToken);
            if (saved is null)
            {
                throw NotFoundException.For("Run", runId);
            }

            return MapDetail(saved);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The run changed concurrently; retry the request.");
        }
        catch (DomainException ex)
        {
            throw new ConflictException(ex.Message);
        }
    }

    private async Task<RunDetailDto?> FindIdempotentRunAsync(Guid definitionId, string key, string requestHash, CancellationToken cancellationToken)
    {
        var record = await TryReadCacheAsync(definitionId, key, cancellationToken);
        if (record is null)
        {
            var row = await _db.WorkflowRuns.AsNoTracking()
                .Where(r => r.WorkflowDefinitionId == definitionId && r.IdempotencyKey == key)
                .Select(r => new { r.Id, r.RequestHash })
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                return null;
            }

            record = new IdempotencyRecord(row.Id, row.RequestHash ?? string.Empty);
            await TryCacheAsync(definitionId, key, record, cancellationToken);
        }

        if (IdempotencyPolicy.Evaluate(record.RequestHash, requestHash) == IdempotencyOutcome.Conflict)
        {
            throw new ConflictException($"Idempotency-Key '{key}' was already used with a different request payload.");
        }

        _logger.LogInformation("Idempotent replay for key {IdempotencyKey} -> run {RunId}", key, record.RunId);
        return await GetAsync(record.RunId, cancellationToken);
    }

    private async Task<IdempotencyRecord?> TryReadCacheAsync(Guid definitionId, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _idempotency.GetAsync(definitionId, key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Idempotency cache read failed; falling back to the database");
            return null;
        }
    }

    private async Task TryCacheAsync(Guid definitionId, string key, IdempotencyRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _idempotency.SetAsync(definitionId, key, record, _options.IdempotencyCacheTtl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Idempotency cache write failed; the database remains authoritative");
        }
    }
}

public enum IdempotencyOutcome
{
    /// <summary>Same key, same payload: return the existing run.</summary>
    Replay,

    /// <summary>Same key, different payload: reject with 409.</summary>
    Conflict,
}

public static class IdempotencyPolicy
{
    public static IdempotencyOutcome Evaluate(string storedRequestHash, string requestHash) =>
        string.Equals(storedRequestHash, requestHash, StringComparison.Ordinal)
            ? IdempotencyOutcome.Replay
            : IdempotencyOutcome.Conflict;
}
