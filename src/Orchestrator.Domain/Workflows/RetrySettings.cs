using Orchestrator.Domain.Common;

namespace Orchestrator.Domain.Workflows;

/// <summary>
/// Per-step retry configuration (value object). Delays grow exponentially from <see cref="InitialDelay"/> by
/// <see cref="BackoffMultiplier"/>, are capped at <see cref="MaxDelay"/>, and are randomised by +/- <see cref="JitterFactor"/>.
/// </summary>
public sealed record RetrySettings
{
    public const int MaxAllowedAttempts = 25;
    public const double MaxAllowedMultiplier = 10.0;

    public RetrySettings(int maxAttempts, TimeSpan initialDelay, double backoffMultiplier, TimeSpan maxDelay, double jitterFactor)
    {
        if (maxAttempts < 1 || maxAttempts > MaxAllowedAttempts)
        {
            throw new DomainException($"maxAttempts must be between 1 and {MaxAllowedAttempts}.");
        }

        if (initialDelay < TimeSpan.Zero)
        {
            throw new DomainException("initialDelay must not be negative.");
        }

        if (double.IsNaN(backoffMultiplier) || backoffMultiplier < 1.0 || backoffMultiplier > MaxAllowedMultiplier)
        {
            throw new DomainException($"backoffMultiplier must be between 1.0 and {MaxAllowedMultiplier}.");
        }

        if (maxDelay < initialDelay)
        {
            throw new DomainException("maxDelay must be greater than or equal to initialDelay.");
        }

        if (double.IsNaN(jitterFactor) || jitterFactor < 0.0 || jitterFactor > 1.0)
        {
            throw new DomainException("jitter must be between 0.0 and 1.0.");
        }

        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay;
        BackoffMultiplier = backoffMultiplier;
        MaxDelay = maxDelay;
        JitterFactor = jitterFactor;
    }

    /// <summary>Total attempts including the first one (1 = no retries).</summary>
    public int MaxAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public double BackoffMultiplier { get; }

    public TimeSpan MaxDelay { get; }

    /// <summary>0 = no jitter; 0.2 = the delay is randomised within +/-20% (never above <see cref="MaxDelay"/>).</summary>
    public double JitterFactor { get; }

    public static RetrySettings Default { get; } =
        new(3, TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromMinutes(1), 0.2);
}
