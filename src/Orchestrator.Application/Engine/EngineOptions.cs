namespace Orchestrator.Application.Engine;

/// <summary>Configuration section <c>Engine</c>.</summary>
public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>Upper bound on steps per definition (validated at creation).</summary>
    public int MaxStepsPerWorkflow { get; set; } = 50;

    /// <summary>How long idempotency decisions are cached (the database remains the source of truth).</summary>
    public TimeSpan IdempotencyCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How far in the future a run may be scheduled.</summary>
    public TimeSpan MaxScheduleAhead { get; set; } = TimeSpan.FromDays(365);
}

/// <summary>Configuration section <c>Scheduler</c> (the run coordinator loop).</summary>
public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum schedules / runs handled per phase per tick.</summary>
    public int BatchSize { get; set; } = 50;
}

/// <summary>Configuration section <c>Worker</c> (step execution loop).</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public bool Enabled { get; set; } = true;

    /// <summary>Optional stable worker id; defaults to machine:pid:random.</summary>
    public string? WorkerId { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum steps executed concurrently by this process.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>Lease length granted by a claim and by each heartbeat.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a running step extends its lease and checks for run cancellation. Must be &lt; LeaseDuration.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Candidates read per claim attempt.</summary>
    public int ClaimBatchSize { get; set; } = 20;

    /// <summary>Expired leases recovered per tick.</summary>
    public int RecoveryBatchSize { get; set; } = 50;

    /// <summary>Expiry of the per-run distributed lock (held only during bookkeeping, never during handler execution).</summary>
    public TimeSpan RunLockExpiry { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long result recording waits for the run lock before proceeding on optimistic concurrency alone.</summary>
    public TimeSpan RunLockWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
