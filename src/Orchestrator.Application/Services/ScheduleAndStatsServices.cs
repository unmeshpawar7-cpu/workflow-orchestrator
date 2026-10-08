using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Common;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Engine;
using Orchestrator.Application.Validation;
using Orchestrator.Domain.Common;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Scheduling;

namespace Orchestrator.Application.Services;

public interface IScheduleService
{
    Task<ScheduleDto> CreateAsync(Guid workflowDefinitionId, CreateScheduleRequest request, CancellationToken cancellationToken);

    Task<ScheduleDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedResult<ScheduleDto>> ListAsync(Guid? workflowDefinitionId, PageRequest page, CancellationToken cancellationToken);

    Task<ScheduleDto> UpdateAsync(Guid id, UpdateScheduleRequest request, CancellationToken cancellationToken);
}

public sealed class ScheduleService : IScheduleService
{
    private readonly IOrchestratorDbContext _db;
    private readonly IDefinitionProvider _definitions;
    private readonly IValidator<CreateScheduleRequest> _createValidator;
    private readonly IValidator<UpdateScheduleRequest> _updateValidator;
    private readonly TimeProvider _timeProvider;

    public ScheduleService(
        IOrchestratorDbContext db,
        IDefinitionProvider definitions,
        IValidator<CreateScheduleRequest> createValidator,
        IValidator<UpdateScheduleRequest> updateValidator,
        TimeProvider timeProvider)
    {
        _db = db;
        _definitions = definitions;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _timeProvider = timeProvider;
    }

    public async Task<ScheduleDto> CreateAsync(Guid workflowDefinitionId, CreateScheduleRequest request, CancellationToken cancellationToken)
    {
        await _createValidator.EnsureValidAsync(request, cancellationToken);
        var definition = await _definitions.GetAsync(workflowDefinitionId, cancellationToken);
        var schedule = Schedule.Create(
            definition.Id,
            JsonText.FromElement(request.Input, "{}"),
            request.IntervalSeconds,
            UtcTime.Normalize(request.RunAt),
            request.Enabled,
            _timeProvider.GetUtcNow().UtcDateTime);
        _db.Schedules.Add(schedule);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(schedule);
    }

    public async Task<ScheduleDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var schedule = await _db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw NotFoundException.For("Schedule", id);
        return ToDto(schedule);
    }

    public async Task<PagedResult<ScheduleDto>> ListAsync(Guid? workflowDefinitionId, PageRequest page, CancellationToken cancellationToken)
    {
        var query = _db.Schedules.AsNoTracking();
        if (workflowDefinitionId is Guid definitionId)
        {
            query = query.Where(s => s.WorkflowDefinitionId == definitionId);
        }

        var result = await query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToPagedResultAsync(page, cancellationToken);
        return result.Map(ToDto);
    }

    public async Task<ScheduleDto> UpdateAsync(Guid id, UpdateScheduleRequest request, CancellationToken cancellationToken)
    {
        await _updateValidator.EnsureValidAsync(request, cancellationToken);
        var schedule = await _db.Schedules.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw NotFoundException.For("Schedule", id);
        try
        {
            if (request.Enabled == true)
            {
                schedule.Enable(_timeProvider.GetUtcNow().UtcDateTime);
            }
            else
            {
                schedule.Disable();
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex)
        {
            throw new ConflictException(ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The schedule changed concurrently (it may have just fired); retry the request.");
        }

        return ToDto(schedule);
    }

    private static ScheduleDto ToDto(Schedule s) =>
        new(s.Id, s.WorkflowDefinitionId, JsonText.ToElement(s.InputJson), s.IntervalSeconds, s.RunAt, s.NextRunAt, s.Enabled, s.CreatedAt, s.LastFiredAt);
}

public interface IStatsService
{
    Task<StatsDto> GetAsync(CancellationToken cancellationToken);
}

/// <summary>All figures come from aggregate SQL (GROUP BY / AVG / SUM); nothing is loaded into memory.</summary>
public sealed class StatsService : IStatsService
{
    private readonly IOrchestratorDbContext _db;
    private readonly TimeProvider _timeProvider;

    public StatsService(IOrchestratorDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<StatsDto> GetAsync(CancellationToken cancellationToken)
    {
        var runCounts = await _db.WorkflowRuns.AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var stepCounts = await _db.StepRuns.AsNoTracking()
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var averageDuration = await _db.StepRuns.AsNoTracking()
            .Where(s => s.Status == StepRunStatus.Succeeded && s.DurationMs != null)
            .AverageAsync(s => s.DurationMs, cancellationToken);

        var retried = _db.StepRuns.AsNoTracking().Where(s => s.Attempt > 1);
        var stepsThatRetried = await retried.CountAsync(cancellationToken);
        var totalRetries = await retried.SumAsync(s => s.Attempt - 1, cancellationToken);
        var definitionCount = await _db.WorkflowDefinitions.CountAsync(cancellationToken);
        var enabledSchedules = await _db.Schedules.CountAsync(s => s.Enabled, cancellationToken);

        return new StatsDto(
            Enum.GetValues<WorkflowRunStatus>().ToDictionary(
                s => s.ToString(),
                s => runCounts.FirstOrDefault(c => c.Status == s)?.Count ?? 0),
            Enum.GetValues<StepRunStatus>().ToDictionary(
                s => s.ToString(),
                s => stepCounts.FirstOrDefault(c => c.Status == s)?.Count ?? 0),
            averageDuration.HasValue ? Math.Round(averageDuration.Value, 1) : (double?)null,
            stepsThatRetried,
            totalRetries,
            definitionCount,
            enabledSchedules,
            _timeProvider.GetUtcNow().UtcDateTime);
    }
}
