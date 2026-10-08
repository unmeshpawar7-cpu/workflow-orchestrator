namespace Orchestrator.Domain.Workflows;

/// <summary>
/// Pure retry/backoff calculator. All randomness and time come from injected sources so the maths is
/// deterministic under test:
/// <code>
/// base(n)  = min(initialDelay * multiplier^(n-1), maxDelay)          n = attempts already made (>= 1)
/// delay(n) = min(base(n) * (1 + jitter * (2u - 1)), maxDelay)        u ~ U[0, 1)
/// </code>
/// </summary>
public sealed class RetryPolicy
{
    private readonly RetrySettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _randomSource;

    public RetryPolicy(RetrySettings settings, TimeProvider timeProvider, Func<double>? randomSource = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _randomSource = randomSource ?? Random.Shared.NextDouble;
    }

    public RetrySettings Settings => _settings;

    /// <summary>True when another attempt is allowed after <paramref name="attemptsMade"/> attempts.</summary>
    public bool ShouldRetry(int attemptsMade) => attemptsMade < _settings.MaxAttempts;

    /// <summary>Exponential delay before the next attempt, without jitter, capped at <c>MaxDelay</c>.</summary>
    public TimeSpan GetBaseDelay(int attemptsMade)
    {
        if (attemptsMade < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptsMade), "At least one attempt must have been made.");
        }

        var initialMs = _settings.InitialDelay.TotalMilliseconds;
        var maxMs = _settings.MaxDelay.TotalMilliseconds;
        var exponential = initialMs * Math.Pow(_settings.BackoffMultiplier, attemptsMade - 1);

        // Math.Pow can overflow to +Infinity for large attempt counts; Math.Min handles that.
        var cappedMs = double.IsNaN(exponential) ? maxMs : Math.Min(exponential, maxMs);
        return TimeSpan.FromMilliseconds(cappedMs);
    }

    /// <summary>Delay with jitter applied, still never above <c>MaxDelay</c> and never negative.</summary>
    public TimeSpan GetDelay(int attemptsMade)
    {
        var baseMs = GetBaseDelay(attemptsMade).TotalMilliseconds;
        var sample = Math.Clamp(_randomSource(), 0.0, 1.0);
        var factor = 1.0 + (_settings.JitterFactor * ((2.0 * sample) - 1.0));
        var jitteredMs = Math.Min(baseMs * factor, _settings.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(0.0, jitteredMs));
    }

    /// <summary>UTC time of the next attempt, or <c>null</c> when the retry budget is exhausted.</summary>
    public DateTime? GetNextAttemptAt(int attemptsMade)
    {
        if (!ShouldRetry(attemptsMade))
        {
            return null;
        }

        return _timeProvider.GetUtcNow().UtcDateTime + GetDelay(attemptsMade);
    }
}
