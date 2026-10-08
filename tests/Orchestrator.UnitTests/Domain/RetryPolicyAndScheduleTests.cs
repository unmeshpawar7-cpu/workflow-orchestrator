using FluentAssertions;
using Orchestrator.Domain.Common;
using Orchestrator.Domain.Scheduling;
using Orchestrator.Domain.Workflows;
using Orchestrator.UnitTests.TestSupport;

namespace Orchestrator.UnitTests.Domain;

public sealed class RetryPolicyTests
{
    private static readonly RetrySettings Settings =
        new(maxAttempts: 5, initialDelay: TimeSpan.FromSeconds(1), backoffMultiplier: 2.0, maxDelay: TimeSpan.FromSeconds(10), jitterFactor: 0.2);

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(5, 10000)]
    [InlineData(60, 10000)]
    public void Base_delay_grows_exponentially_and_is_capped(int attemptsMade, double expectedMs)
    {
        var policy = new RetryPolicy(Settings, ManualTimeProvider.Default(), () => 0.5);

        policy.GetBaseDelay(attemptsMade).TotalMilliseconds.Should().Be(expectedMs);
    }

    [Fact]
    public void Huge_attempt_counts_do_not_overflow()
    {
        var policy = new RetryPolicy(Settings, ManualTimeProvider.Default());

        policy.GetBaseDelay(10_000).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0.0, 1600)]
    [InlineData(0.5, 2000)]
    [InlineData(1.0, 2400)]
    public void Jitter_is_applied_symmetrically_around_the_base_delay(double sample, double expectedMs)
    {
        var policy = new RetryPolicy(Settings, ManualTimeProvider.Default(), () => sample);

        policy.GetDelay(2).TotalMilliseconds.Should().BeApproximately(expectedMs, 0.001);
    }

    [Fact]
    public void Jittered_delay_stays_within_bounds_and_never_exceeds_the_cap()
    {
        var random = new Random(42);
        var policy = new RetryPolicy(Settings, ManualTimeProvider.Default(), random.NextDouble);

        for (var i = 0; i < 1000; i++)
        {
            var attempts = (i % 6) + 1;
            var baseMs = policy.GetBaseDelay(attempts).TotalMilliseconds;
            var delayMs = policy.GetDelay(attempts).TotalMilliseconds;

            delayMs.Should().BeGreaterThanOrEqualTo(baseMs * 0.8 - 0.001);
            delayMs.Should().BeLessThanOrEqualTo(Math.Min(baseMs * 1.2, 10_000) + 0.001);
        }
    }

    [Fact]
    public void Next_attempt_time_uses_the_injected_clock_and_stops_when_budget_is_exhausted()
    {
        var clock = ManualTimeProvider.Default();
        var policy = new RetryPolicy(Settings, clock, () => 0.5);

        policy.GetNextAttemptAt(1).Should().Be(clock.UtcNow.AddSeconds(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        policy.GetNextAttemptAt(3).Should().Be(clock.UtcNow.AddSeconds(4));
        policy.ShouldRetry(4).Should().BeTrue();
        policy.GetNextAttemptAt(5).Should().BeNull();
    }

    [Fact]
    public void Invalid_inputs_are_rejected()
    {
        var policy = new RetryPolicy(Settings, ManualTimeProvider.Default());
        policy.Invoking(p => p.GetBaseDelay(0)).Should().Throw<ArgumentOutOfRangeException>();

        var act = () => new RetrySettings(0, TimeSpan.Zero, 2, TimeSpan.Zero, 0);
        act.Should().Throw<DomainException>();
        var badJitter = () => new RetrySettings(1, TimeSpan.Zero, 2, TimeSpan.Zero, 1.5);
        badJitter.Should().Throw<DomainException>();
        var badCap = () => new RetrySettings(1, TimeSpan.FromSeconds(5), 2, TimeSpan.FromSeconds(1), 0);
        badCap.Should().Throw<DomainException>();
    }
}

public sealed class ScheduleTests
{
    private static readonly DateTime Now = Specs.Now;

    [Fact]
    public void Requires_an_interval_or_a_run_time()
    {
        var act = () => Schedule.Create(Guid.NewGuid(), "{}", null, null, true, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Interval_schedule_fires_and_coalesces_missed_slots()
    {
        var schedule = Schedule.Create(Guid.NewGuid(), "{}", 60, null, true, Now);
        schedule.NextRunAt.Should().Be(Now.AddSeconds(60));
        schedule.IsDue(Now).Should().BeFalse();

        var late = Now.AddMinutes(10);
        var fireTime = schedule.Fire(late);

        fireTime.Should().Be(Now.AddSeconds(60));
        schedule.NextRunAt.Should().Be(late.AddSeconds(60), "missed slots are coalesced instead of replayed");
        schedule.Enabled.Should().BeTrue();
    }

    [Fact]
    public void One_off_schedule_disables_itself_and_cannot_be_re_enabled()
    {
        var schedule = Schedule.Create(Guid.NewGuid(), null, null, Now.AddMinutes(5), true, Now);

        schedule.Fire(Now.AddMinutes(5));

        schedule.Enabled.Should().BeFalse();
        schedule.NextRunAt.Should().BeNull();
        schedule.Invoking(s => s.Enable(Now)).Should().Throw<DomainException>();
        schedule.Invoking(s => s.Fire(Now.AddHours(1))).Should().Throw<DomainException>();
    }

    [Fact]
    public void Disabling_and_re_enabling_an_interval_schedule_restarts_its_cadence()
    {
        var schedule = Schedule.Create(Guid.NewGuid(), "{}", 30, null, true, Now);
        var version = schedule.Version;

        schedule.Disable();
        schedule.IsDue(Now.AddHours(1)).Should().BeFalse();
        schedule.Enable(Now.AddHours(1));

        schedule.NextRunAt.Should().Be(Now.AddHours(1).AddSeconds(30));
        schedule.Version.Should().NotBe(version);
    }
}
