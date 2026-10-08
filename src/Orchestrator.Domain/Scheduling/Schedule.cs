using Orchestrator.Domain.Common;

namespace Orchestrator.Domain.Scheduling;

/// <summary>
/// Creates runs of a definition on an interval, at a one-off time, or both (first at <see cref="RunAt"/>, then
/// every <see cref="IntervalSeconds"/>). Missed fires (e.g. while the scheduler was down) are coalesced into
/// one run rather than replayed as a burst.
/// </summary>
public sealed class Schedule
{
    public const int MinIntervalSeconds = 1;
    public const int MaxIntervalSeconds = 31 * 24 * 3600;

    private Schedule()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkflowDefinitionId { get; private set; }

    public string InputJson { get; private set; } = "{}";

    public int? IntervalSeconds { get; private set; }

    public DateTime? RunAt { get; private set; }

    public DateTime? NextRunAt { get; private set; }

    public bool Enabled { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? LastFiredAt { get; private set; }

    public Guid Version { get; private set; }

    public static Schedule Create(Guid workflowDefinitionId, string? inputJson, int? intervalSeconds, DateTime? runAt, bool enabled, DateTime now)
    {
        if (intervalSeconds is null && runAt is null)
        {
            throw new DomainException("A schedule needs an interval, a one-off run time, or both.");
        }

        if (intervalSeconds is < MinIntervalSeconds or > MaxIntervalSeconds)
        {
            throw new DomainException($"intervalSeconds must be between {MinIntervalSeconds} and {MaxIntervalSeconds}.");
        }

        return new Schedule
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowDefinitionId,
            InputJson = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson,
            IntervalSeconds = intervalSeconds,
            RunAt = runAt,
            NextRunAt = runAt ?? now.AddSeconds(intervalSeconds!.Value),
            Enabled = enabled,
            CreatedAt = now,
            Version = Guid.NewGuid(),
        };
    }

    public bool IsDue(DateTime now) => Enabled && NextRunAt is DateTime next && next <= now;

    /// <summary>Consumes the due fire and returns its nominal time (used for the run's ScheduledFor).</summary>
    public DateTime Fire(DateTime now)
    {
        if (!IsDue(now))
        {
            throw new DomainException("Schedule is not due.");
        }

        var fireTime = NextRunAt!.Value;
        LastFiredAt = now;
        if (IntervalSeconds is int interval)
        {
            var next = fireTime.AddSeconds(interval);
            NextRunAt = next <= now ? now.AddSeconds(interval) : next;
        }
        else
        {
            NextRunAt = null;
            Enabled = false;
        }

        Version = Guid.NewGuid();
        return fireTime;
    }

    public void Enable(DateTime now)
    {
        if (Enabled)
        {
            return;
        }

        if (IntervalSeconds is int interval)
        {
            if (NextRunAt is null || NextRunAt < now)
            {
                NextRunAt = now.AddSeconds(interval);
            }
        }
        else if (NextRunAt is null)
        {
            throw new DomainException("This one-off schedule has already fired and cannot be re-enabled.");
        }

        Enabled = true;
        Version = Guid.NewGuid();
    }

    public void Disable()
    {
        if (!Enabled)
        {
            return;
        }

        Enabled = false;
        Version = Guid.NewGuid();
    }
}
