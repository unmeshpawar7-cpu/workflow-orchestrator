using System.Text.Json;
using Orchestrator.Domain.Common;

namespace Orchestrator.Domain.Workflows;

/// <summary>Input used to create a <see cref="StepDefinition"/>.</summary>
public sealed record StepDefinitionSpec(
    string Key,
    string HandlerType,
    string? ConfigJson,
    IReadOnlyList<string> DependsOn,
    int TimeoutSeconds,
    RetrySettings Retry,
    string? CompensationHandlerType = null,
    string? CompensationConfigJson = null)
{
    public GraphNode ToGraphNode() => new(Key, DependsOn, ConfigJson, CompensationConfigJson);
}

/// <summary>
/// A step inside an immutable <see cref="WorkflowDefinition"/>. Retry settings are flattened into columns;
/// <see cref="Retry"/> rebuilds the value object.
/// </summary>
public sealed class StepDefinition
{
    public const int MaxHandlerTypeLength = 100;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;

    private IReadOnlyList<string>? _dependsOnCache;

    private StepDefinition()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkflowDefinitionId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    /// <summary>Position in the deterministic topological order (0-based). Compensation runs in reverse.</summary>
    public int Ordinal { get; private set; }

    public string HandlerType { get; private set; } = string.Empty;

    public string? HandlerConfigJson { get; private set; }

    public string DependsOnJson { get; private set; } = "[]";

    public int TimeoutSeconds { get; private set; }

    public int MaxAttempts { get; private set; }

    public int InitialDelayMs { get; private set; }

    public double BackoffMultiplier { get; private set; }

    public int MaxDelayMs { get; private set; }

    public double JitterFactor { get; private set; }

    public string? CompensationHandlerType { get; private set; }

    public string? CompensationConfigJson { get; private set; }

    public IReadOnlyList<string> DependsOn =>
        _dependsOnCache ??= JsonSerializer.Deserialize<List<string>>(DependsOnJson) ?? new List<string>();

    public bool HasCompensation => !string.IsNullOrWhiteSpace(CompensationHandlerType);

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    public RetrySettings Retry => new(
        MaxAttempts,
        TimeSpan.FromMilliseconds(InitialDelayMs),
        BackoffMultiplier,
        TimeSpan.FromMilliseconds(MaxDelayMs),
        JitterFactor);

    internal static StepDefinition Create(Guid workflowDefinitionId, StepDefinitionSpec spec, int ordinal)
    {
        if (string.IsNullOrWhiteSpace(spec.HandlerType) || spec.HandlerType.Length > MaxHandlerTypeLength)
        {
            throw new DomainException($"Step '{spec.Key}' needs a handler type of at most {MaxHandlerTypeLength} characters.");
        }

        if (spec.TimeoutSeconds < MinTimeoutSeconds || spec.TimeoutSeconds > MaxTimeoutSeconds)
        {
            throw new DomainException(
                $"Step '{spec.Key}' timeout must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.");
        }

        var dependsOn = spec.DependsOn.Distinct(StringComparer.Ordinal).ToList();
        return new StepDefinition
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowDefinitionId,
            Key = spec.Key,
            Ordinal = ordinal,
            HandlerType = spec.HandlerType,
            HandlerConfigJson = spec.ConfigJson,
            DependsOnJson = JsonSerializer.Serialize(dependsOn),
            TimeoutSeconds = spec.TimeoutSeconds,
            MaxAttempts = spec.Retry.MaxAttempts,
            InitialDelayMs = (int)Math.Min(int.MaxValue, spec.Retry.InitialDelay.TotalMilliseconds),
            BackoffMultiplier = spec.Retry.BackoffMultiplier,
            MaxDelayMs = (int)Math.Min(int.MaxValue, spec.Retry.MaxDelay.TotalMilliseconds),
            JitterFactor = spec.Retry.JitterFactor,
            CompensationHandlerType = string.IsNullOrWhiteSpace(spec.CompensationHandlerType) ? null : spec.CompensationHandlerType,
            CompensationConfigJson = string.IsNullOrWhiteSpace(spec.CompensationHandlerType) ? null : spec.CompensationConfigJson,
        };
    }
}
