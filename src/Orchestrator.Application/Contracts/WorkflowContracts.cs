using System.Text.Json;

namespace Orchestrator.Application.Contracts;

/// <summary>Request to register a workflow definition. Re-using an existing name creates the next version.</summary>
public sealed class CreateWorkflowDefinitionRequest
{
    /// <summary>Workflow name (letters, digits, space, '.', '_' or '-').</summary>
    /// <example>order-fulfilment</example>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional human-readable description.</summary>
    /// <example>Reserve stock, charge payment and ship; compensates on failure.</example>
    public string? Description { get; set; }

    /// <summary>Maximum simultaneously active runs of this definition (1-1000).</summary>
    /// <example>10</example>
    public int MaxConcurrentRuns { get; set; } = 10;

    /// <summary>The steps of the DAG. Order is irrelevant; dependencies define execution order.</summary>
    public List<StepDefinitionRequest> Steps { get; set; } = new();
}

/// <summary>A single step of a workflow definition.</summary>
public sealed class StepDefinitionRequest
{
    /// <summary>Unique key within the workflow (letters, digits, '-', '_').</summary>
    /// <example>reserve-inventory</example>
    public string Key { get; set; } = string.Empty;

    /// <summary>Handler type: http, delay, transform, log or fault-injection.</summary>
    /// <example>http</example>
    public string Handler { get; set; } = string.Empty;

    /// <summary>Handler configuration (JSON object). String values may contain <c>{{input.x}}</c> or <c>{{steps.key.output.x}}</c> templates.</summary>
    public JsonElement? Config { get; set; }

    /// <summary>Keys of steps that must succeed before this one becomes ready.</summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>Per-attempt timeout in seconds (1-3600).</summary>
    /// <example>30</example>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Retry policy; defaults to 3 attempts, 1s initial delay, x2 backoff, 60s cap, 20% jitter.</summary>
    public RetryPolicyRequest? Retry { get; set; }

    /// <summary>Optional saga compensation executed if the run fails after this step succeeded.</summary>
    public CompensationRequest? Compensation { get; set; }
}

/// <summary>Exponential backoff settings.</summary>
public sealed class RetryPolicyRequest
{
    /// <summary>Total attempts including the first (1-25).</summary>
    /// <example>3</example>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before the first retry in milliseconds.</summary>
    /// <example>1000</example>
    public int InitialDelayMs { get; set; } = 1000;

    /// <summary>Multiplier applied per attempt (1.0-10.0).</summary>
    /// <example>2.0</example>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>Upper bound for any delay in milliseconds.</summary>
    /// <example>60000</example>
    public int MaxDelayMs { get; set; } = 60000;

    /// <summary>Relative jitter (0.0-1.0); 0.2 randomises each delay by +/-20%.</summary>
    /// <example>0.2</example>
    public double Jitter { get; set; } = 0.2;
}

/// <summary>Compensation handler for a step.</summary>
public sealed class CompensationRequest
{
    /// <summary>Handler type used to undo the step.</summary>
    /// <example>http</example>
    public string Handler { get; set; } = string.Empty;

    /// <summary>Handler configuration; may reference the step's own output via <c>{{steps.&lt;self&gt;.output.x}}</c>.</summary>
    public JsonElement? Config { get; set; }
}

public sealed record RetryPolicyDto(int MaxAttempts, int InitialDelayMs, double BackoffMultiplier, int MaxDelayMs, double Jitter);

public sealed record CompensationDto(string Handler, JsonElement? Config);

public sealed record StepDefinitionDto(
    string Key,
    string Handler,
    JsonElement? Config,
    IReadOnlyList<string> DependsOn,
    int Ordinal,
    int TimeoutSeconds,
    RetryPolicyDto Retry,
    CompensationDto? Compensation);

public sealed record WorkflowDefinitionDto(
    Guid Id,
    string Name,
    int Version,
    string? Description,
    int MaxConcurrentRuns,
    DateTime CreatedAt,
    IReadOnlyList<StepDefinitionDto> Steps);

public sealed record WorkflowDefinitionSummaryDto(
    Guid Id,
    string Name,
    int Version,
    string? Description,
    int MaxConcurrentRuns,
    int StepCount,
    DateTime CreatedAt);
