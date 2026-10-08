using Orchestrator.Domain.Common;

namespace Orchestrator.Domain.Runs;

public enum ExecutionEventType
{
    RunCreated = 0,
    RunStarted = 1,
    RunSucceeded = 2,
    RunFailed = 3,
    RunCancelled = 4,
    RunRetried = 5,
    CompensationStarted = 6,
    RunCompensated = 7,
    RunDeadLettered = 8,
    RunRequeued = 9,
    StepReady = 20,
    StepStarted = 21,
    StepSucceeded = 22,
    StepRetryScheduled = 23,
    StepFailed = 24,
    StepSkipped = 25,
    StepCancelled = 26,
    StepLeaseExpired = 27,
    StepReleased = 28,
    StepCompensating = 30,
    StepCompensationStarted = 31,
    StepCompensated = 32,
    StepCompensationRetryScheduled = 33,
    StepCompensationFailed = 34,
}

/// <summary>
/// Append-only execution history / audit record. Never updated or deleted by the application.
/// </summary>
public sealed class ExecutionEvent
{
    public const int MaxMessageLength = 2000;

    private ExecutionEvent()
    {
    }

    /// <summary>Database identity; ordering by it gives the append order.</summary>
    public long Id { get; private set; }

    public Guid WorkflowRunId { get; private set; }

    public string? StepKey { get; private set; }

    public ExecutionEventType EventType { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public string? DataJson { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public static ExecutionEvent Create(
        Guid workflowRunId,
        ExecutionEventType eventType,
        string message,
        DateTime occurredAt,
        string? stepKey = null,
        string? dataJson = null) =>
        new()
        {
            WorkflowRunId = workflowRunId,
            EventType = eventType,
            Message = DomainJson.Truncate(message, MaxMessageLength),
            OccurredAt = occurredAt,
            StepKey = stepKey,
            DataJson = dataJson,
        };
}
