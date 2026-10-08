using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Common;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Engine;
using Orchestrator.Application.Validation;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.Application.Services;

public interface IWorkflowDefinitionService
{
    Task<WorkflowDefinitionDto> CreateAsync(CreateWorkflowDefinitionRequest request, CancellationToken cancellationToken);

    Task<WorkflowDefinitionDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedResult<WorkflowDefinitionSummaryDto>> ListAsync(string? name, PageRequest page, CancellationToken cancellationToken);
}

public sealed class WorkflowDefinitionService : IWorkflowDefinitionService
{
    private readonly IOrchestratorDbContext _db;
    private readonly IValidator<CreateWorkflowDefinitionRequest> _validator;
    private readonly IStepHandlerResolver _handlers;
    private readonly TimeProvider _timeProvider;
    private readonly EngineOptions _options;
    private readonly ILogger<WorkflowDefinitionService> _logger;

    public WorkflowDefinitionService(
        IOrchestratorDbContext db,
        IValidator<CreateWorkflowDefinitionRequest> validator,
        IStepHandlerResolver handlers,
        TimeProvider timeProvider,
        IOptions<EngineOptions> options,
        ILogger<WorkflowDefinitionService> logger)
    {
        _db = db;
        _validator = validator;
        _handlers = handlers;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WorkflowDefinitionDto> CreateAsync(CreateWorkflowDefinitionRequest request, CancellationToken cancellationToken)
    {
        await _validator.EnsureValidAsync(request, cancellationToken);

        var specs = request.Steps.Select(ToSpec).ToList();
        var graph = WorkflowGraph.Validate(specs.Select(s => s.ToGraphNode()).ToList(), _options.MaxStepsPerWorkflow);
        var errors = graph.Errors
            .Select(e => new ValidationFailure(e.StepKey is null ? "steps" : $"steps.{e.StepKey}", e.Message))
            .ToList();

        for (var i = 0; i < request.Steps.Count; i++)
        {
            var step = request.Steps[i];
            if (_handlers.Find(step.Handler) is null)
            {
                errors.Add(new ValidationFailure(
                    $"steps[{i}].handler",
                    $"Unknown handler type '{step.Handler}'. Registered: {string.Join(", ", _handlers.RegisteredTypes)}."));
            }

            if (step.Compensation is not null && _handlers.Find(step.Compensation.Handler) is null)
            {
                errors.Add(new ValidationFailure(
                    $"steps[{i}].compensation.handler",
                    $"Unknown compensation handler type '{step.Compensation.Handler}'."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException("Workflow definition is invalid.", errors);
        }

        var name = request.Name.Trim();
        var latestVersion = await _db.WorkflowDefinitions
            .Where(d => d.Name == name)
            .MaxAsync(d => (int?)d.Version, cancellationToken) ?? 0;

        var definition = WorkflowDefinition.Create(
            name,
            latestVersion + 1,
            request.Description,
            request.MaxConcurrentRuns,
            specs,
            _timeProvider.GetUtcNow().UtcDateTime,
            _options.MaxStepsPerWorkflow);
        _db.WorkflowDefinitions.Add(definition);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(ex, "Concurrent creation of workflow {Name} v{Version}", name, definition.Version);
            throw new ConflictException($"Version {definition.Version} of workflow '{name}' was created concurrently; retry the request.");
        }

        _logger.LogInformation("Created workflow {Name} v{Version} ({DefinitionId}) with {StepCount} steps", name, definition.Version, definition.Id, specs.Count);
        return Map(definition);
    }

    public async Task<WorkflowDefinitionDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var definition = await _db.WorkflowDefinitions.AsNoTracking()
            .Include(d => d.Steps)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw NotFoundException.For("Workflow definition", id);
        return Map(definition);
    }

    public async Task<PagedResult<WorkflowDefinitionSummaryDto>> ListAsync(string? name, PageRequest page, CancellationToken cancellationToken)
    {
        var query = _db.WorkflowDefinitions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(d => d.Name == name);
        }

        return await query
            .OrderBy(d => d.Name)
            .ThenByDescending(d => d.Version)
            .Select(d => new WorkflowDefinitionSummaryDto(d.Id, d.Name, d.Version, d.Description, d.MaxConcurrentRuns, d.Steps.Count(), d.CreatedAt))
            .ToPagedResultAsync(page, cancellationToken);
    }

    internal static StepDefinitionSpec ToSpec(StepDefinitionRequest step)
    {
        var retry = step.Retry ?? new RetryPolicyRequest();
        return new StepDefinitionSpec(
            step.Key,
            step.Handler,
            JsonText.FromElement(step.Config),
            step.DependsOn ?? new List<string>(),
            step.TimeoutSeconds,
            new RetrySettings(
                retry.MaxAttempts,
                TimeSpan.FromMilliseconds(retry.InitialDelayMs),
                retry.BackoffMultiplier,
                TimeSpan.FromMilliseconds(retry.MaxDelayMs),
                retry.Jitter),
            step.Compensation?.Handler,
            JsonText.FromElement(step.Compensation?.Config));
    }

    internal static WorkflowDefinitionDto Map(WorkflowDefinition definition) =>
        new(
            definition.Id,
            definition.Name,
            definition.Version,
            definition.Description,
            definition.MaxConcurrentRuns,
            definition.CreatedAt,
            definition.Steps
                .OrderBy(s => s.Ordinal)
                .Select(s => new StepDefinitionDto(
                    s.Key,
                    s.HandlerType,
                    JsonText.ToElement(s.HandlerConfigJson),
                    s.DependsOn,
                    s.Ordinal,
                    s.TimeoutSeconds,
                    new RetryPolicyDto(s.MaxAttempts, s.InitialDelayMs, s.BackoffMultiplier, s.MaxDelayMs, s.JitterFactor),
                    s.CompensationHandlerType is null
                        ? null
                        : new CompensationDto(s.CompensationHandlerType, JsonText.ToElement(s.CompensationConfigJson))))
                .ToList());
}
