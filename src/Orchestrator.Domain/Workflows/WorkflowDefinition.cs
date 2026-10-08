using Orchestrator.Domain.Common;

namespace Orchestrator.Domain.Workflows;

/// <summary>
/// Immutable, versioned workflow definition (a DAG of steps). There are no mutators: changing a workflow
/// means creating a new version with the same name. (Name, Version) is unique.
/// </summary>
public sealed class WorkflowDefinition
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxConcurrentRunsLimit = 1000;

    private readonly List<StepDefinition> _steps = new();

    private WorkflowDefinition()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int Version { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Maximum number of runs of this definition that may be active (Running/Compensating) at once.</summary>
    public int MaxConcurrentRuns { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public IReadOnlyCollection<StepDefinition> Steps => _steps;

    public static WorkflowDefinition Create(
        string name,
        int version,
        string? description,
        int maxConcurrentRuns,
        IReadOnlyList<StepDefinitionSpec> steps,
        DateTime createdAt,
        int maxSteps = WorkflowGraph.DefaultMaxSteps)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new DomainException($"Workflow name is required and must be at most {MaxNameLength} characters.");
        }

        if (version < 1)
        {
            throw new DomainException("Workflow version must be at least 1.");
        }

        if (maxConcurrentRuns < 1 || maxConcurrentRuns > MaxConcurrentRunsLimit)
        {
            throw new DomainException($"maxConcurrentRuns must be between 1 and {MaxConcurrentRunsLimit}.");
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            throw new DomainException($"Description must be at most {MaxDescriptionLength} characters.");
        }

        ArgumentNullException.ThrowIfNull(steps);
        var validation = WorkflowGraph.Validate(steps.Select(s => s.ToGraphNode()).ToList(), maxSteps);
        if (!validation.IsValid)
        {
            throw new DomainException(
                "Invalid workflow graph: " + string.Join("; ", validation.Errors.Select(e => e.Message)));
        }

        var definition = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Version = version,
            Description = description,
            MaxConcurrentRuns = maxConcurrentRuns,
            CreatedAt = createdAt,
        };

        var ordinals = validation.TopologicalOrder
            .Select((key, position) => (key, position))
            .ToDictionary(x => x.key, x => x.position, StringComparer.Ordinal);

        foreach (var spec in steps)
        {
            definition._steps.Add(StepDefinition.Create(definition.Id, spec, ordinals[spec.Key]));
        }

        return definition;
    }

    public StepDefinition GetStep(string key) =>
        _steps.FirstOrDefault(s => s.Key == key)
        ?? throw new DomainException($"Workflow '{Name}' v{Version} has no step '{key}'.");

    public IReadOnlyList<GraphNode> ToGraph() =>
        _steps.OrderBy(s => s.Ordinal)
            .Select(s => new GraphNode(s.Key, s.DependsOn, s.HandlerConfigJson, s.CompensationConfigJson))
            .ToList();

    /// <summary>True when no other step depends on <paramref name="key"/> (its output becomes part of the run output).</summary>
    public bool IsSink(string key) => !_steps.Any(s => s.DependsOn.Contains(key));
}
