using System.Text.Json;
using Orchestrator.Domain.Runs;

namespace Orchestrator.Application.Contracts;

/// <summary>Request to start a run of a workflow definition.</summary>
public sealed class CreateRunRequest
{
    /// <summary>Run input (any JSON value, usually an object). Available to steps as <c>{{input.x}}</c>.</summary>
    public JsonElement? Input { get; set; }

    /// <summary>Optional UTC time before which the run will not start (delayed execution).</summary>
    /// <example>2026-10-08T12:00:00Z</example>
    public DateTime? ScheduledFor { get; set; }
}

public sealed record StepRunDto(
    string Key,
    int Ordinal,
    StepRunStatus Status,
    int Attempt,
    int CompensationAttempt,
    DateTime? NextAttemptAt,
    string? LeaseOwner,
    DateTime? LeaseExpiresAt,
    JsonElement? Output,
    string? LastError,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    long? DurationMs);

public sealed record RunSummaryDto(
    Guid Id,
    Guid WorkflowDefinitionId,
    WorkflowRunStatus Status,
    string CorrelationId,
    string? IdempotencyKey,
    DateTime ScheduledFor,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Error);

public sealed record RunDetailDto(
    Guid Id,
    Guid WorkflowDefinitionId,
    Guid? ScheduleId,
    WorkflowRunStatus Status,
    string CorrelationId,
    string? IdempotencyKey,
    JsonElement? Input,
    JsonElement? Output,
    string? Error,
    DateTime ScheduledFor,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<StepRunDto> Steps);

/// <summary>Result of run creation. <see cref="Created"/> is false when an idempotent replay returned an existing run.</summary>
public sealed record RunCreationResult(RunDetailDto Run, bool Created);

public sealed record ExecutionEventDto(
    long Id,
    string? StepKey,
    ExecutionEventType EventType,
    string Message,
    JsonElement? Data,
    DateTime OccurredAt);

/// <summary>Filter for run listings.</summary>
public sealed record RunFilter(WorkflowRunStatus? Status, Guid? WorkflowDefinitionId);

/// <summary>Request to create a schedule. Provide an interval, a one-off run time, or both.</summary>
public sealed class CreateScheduleRequest
{
    /// <summary>Input passed to every run created by the schedule.</summary>
    public JsonElement? Input { get; set; }

    /// <summary>Repeat interval in seconds.</summary>
    /// <example>300</example>
    public int? IntervalSeconds { get; set; }

    /// <summary>One-off (or first) run time in UTC.</summary>
    public DateTime? RunAt { get; set; }

    /// <summary>Whether the schedule starts enabled.</summary>
    /// <example>true</example>
    public bool Enabled { get; set; } = true;
}

/// <summary>Partial update of a schedule.</summary>
public sealed class UpdateScheduleRequest
{
    /// <summary>Enable or disable the schedule.</summary>
    /// <example>false</example>
    public bool? Enabled { get; set; }
}

public sealed record ScheduleDto(
    Guid Id,
    Guid WorkflowDefinitionId,
    JsonElement? Input,
    int? IntervalSeconds,
    DateTime? RunAt,
    DateTime? NextRunAt,
    bool Enabled,
    DateTime CreatedAt,
    DateTime? LastFiredAt);

/// <summary>Operational statistics computed with aggregate queries.</summary>
public sealed record StatsDto(
    IReadOnlyDictionary<string, int> RunsByStatus,
    IReadOnlyDictionary<string, int> StepsByStatus,
    double? AverageStepDurationMs,
    int StepsThatRetried,
    int TotalRetries,
    int DefinitionCount,
    int EnabledSchedules,
    DateTime GeneratedAt);
