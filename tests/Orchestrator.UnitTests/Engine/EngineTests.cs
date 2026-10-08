using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Common;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Engine;
using Orchestrator.Application.Services;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Workflows;
using Orchestrator.UnitTests.TestSupport;

namespace Orchestrator.UnitTests.Engine;

/// <summary>
/// Engine-level tests: real coordinator, executor, aggregate and EF Core model over SQLite. The scheduler tick and
/// worker process methods are called directly, and a manual clock makes retry back-offs elapse instantly.
/// </summary>
public sealed class EngineTests
{
    private static WorkflowDefinition Chain(StepDefinitionSpec b) =>
        Specs.Definition(
            Specs.Step("a", handler: "record", compensation: "record"),
            b,
            Specs.Step("c", handler: "record", dependsOn: new[] { "b" }));

    [Fact]
    public async Task Three_step_chain_retries_a_flaky_step_and_succeeds()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Chain(
            Specs.Step("b", handler: "fault-injection", dependsOn: new[] { "a" }, config: """{"mode":"fail","attempts":2}""", maxAttempts: 3)));
        var runId = await harness.CreateRunAsync(definition);

        var run = await harness.DriveAsync(runId);

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        run.Steps.Should().OnlyContain(s => s.Status == StepRunStatus.Succeeded);
        run.Steps.Single(s => s.StepKey == "b").Attempt.Should().Be(3);
        run.OutputJson.Should().Contain("\"c\"");
        harness.Recording.Calls.Select(c => c.StepKey).Should().Equal("a", "c");

        var events = await harness.GetEventsAsync(runId);
        events.Count(e => e.EventType == ExecutionEventType.StepRetryScheduled).Should().Be(2);
        events.Count(e => e.EventType == ExecutionEventType.StepStarted).Should().Be(5);
        events.Last().EventType.Should().Be(ExecutionEventType.RunSucceeded);
    }

    [Fact]
    public async Task Retry_waits_for_the_backoff_delay_before_the_step_is_claimable_again()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(
            Specs.Step("flaky", handler: "fault-injection", config: """{"attempts":1}""", initialDelayMs: 4000)));
        var runId = await harness.CreateRunAsync(definition);
        await harness.TickAsync();

        (await harness.ProcessNextAsync()).Should().Be(StepExecutionOutcome.RetryScheduled);
        var retrying = (await harness.GetRunAsync(runId)).Steps.Single();
        retrying.Status.Should().Be(StepRunStatus.Retrying);
        retrying.NextAttemptAt.Should().Be(harness.Clock.UtcNow.AddSeconds(4));

        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        (await harness.ProcessNextAsync()).Should().BeNull("the back-off has not elapsed yet");

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        (await harness.ProcessNextAsync()).Should().Be(StepExecutionOutcome.Succeeded);
    }

    [Fact]
    public async Task Permanently_failing_step_triggers_compensation_of_completed_steps()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Chain(
            Specs.Step("b", handler: "fault-injection", dependsOn: new[] { "a" }, config: """{"mode":"fail"}""", maxAttempts: 2, initialDelayMs: 100)));
        var runId = await harness.CreateRunAsync(definition);

        var run = await harness.DriveAsync(runId);

        run.Status.Should().Be(WorkflowRunStatus.Compensated);
        run.Steps.Single(s => s.StepKey == "a").Status.Should().Be(StepRunStatus.Compensated);
        run.Steps.Single(s => s.StepKey == "b").Status.Should().Be(StepRunStatus.Failed);
        run.Steps.Single(s => s.StepKey == "b").Attempt.Should().Be(2);
        run.Steps.Single(s => s.StepKey == "c").Status.Should().Be(StepRunStatus.Skipped);
        harness.Recording.Calls.Should().Equal(("a", false, 1), ("a", true, 1));

        var types = (await harness.GetEventsAsync(runId)).Select(e => e.EventType).ToList();
        types.Should().ContainInOrder(
            ExecutionEventType.StepFailed,
            ExecutionEventType.RunFailed,
            ExecutionEventType.CompensationStarted,
            ExecutionEventType.StepCompensationStarted,
            ExecutionEventType.StepCompensated,
            ExecutionEventType.RunCompensated);
    }

    [Fact]
    public async Task Compensation_that_keeps_failing_dead_letters_the_run()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(
            Specs.Step("a", handler: "record", compensation: "fault-injection", compensationConfig: """{"mode":"fail"}""", maxAttempts: 2, initialDelayMs: 100),
            Specs.Step("b", handler: "fault-injection", dependsOn: new[] { "a" }, config: """{"retryable":false}""")));
        var runId = await harness.CreateRunAsync(definition);

        var run = await harness.DriveAsync(runId);

        run.Status.Should().Be(WorkflowRunStatus.DeadLettered);
        var a = run.Steps.Single(s => s.StepKey == "a");
        a.Status.Should().Be(StepRunStatus.CompensationFailed);
        a.CompensationAttempt.Should().Be(2, "compensations use the step's retry policy");
        run.Steps.Single(s => s.StepKey == "b").Attempt.Should().Be(1, "non-retryable failures are not retried");
    }

    [Fact]
    public async Task Step_exceeding_its_timeout_is_cancelled_and_retried()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(
            Specs.Step("slow", handler: "fault-injection", config: """{"mode":"hang","attempts":1}""", timeoutSeconds: 1, maxAttempts: 2, initialDelayMs: 0)));
        var runId = await harness.CreateRunAsync(definition);

        var run = await harness.DriveAsync(runId);

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        run.Steps.Single().Attempt.Should().Be(2);
        var retry = (await harness.GetEventsAsync(runId)).Single(e => e.EventType == ExecutionEventType.StepRetryScheduled);
        retry.Message.Should().Contain("Timed out after 1s");
    }

    [Fact]
    public async Task Expired_lease_is_recovered_and_the_stale_worker_is_fenced_out()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(Specs.Step("a", handler: "record")));
        var runId = await harness.CreateRunAsync(definition);
        await harness.TickAsync();

        var staleClaim = await harness.ClaimAsync("crashed-worker");
        staleClaim.Should().NotBeNull();
        (await harness.RecoverAsync()).Should().Be(0, "the lease has not expired yet");

        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        (await harness.RecoverAsync()).Should().Be(1);

        var recovered = (await harness.GetRunAsync(runId)).Steps.Single();
        recovered.Status.Should().Be(StepRunStatus.Retrying);
        recovered.LeaseOwner.Should().BeNull();
        recovered.LastError.Should().Contain("Lease expired");

        (await harness.ExecuteAsync(staleClaim!)).Should().Be(StepExecutionOutcome.LeaseLost);
        harness.Recording.Calls.Should().BeEmpty("a fenced-out claim must not run the handler");

        var run = await harness.DriveAsync(runId);
        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        run.Steps.Single().Attempt.Should().Be(2);
        (await harness.GetEventsAsync(runId)).Should().Contain(e => e.EventType == ExecutionEventType.StepLeaseExpired);
    }

    [Fact]
    public async Task Only_one_worker_can_claim_a_step()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(Specs.Step("a", handler: "record")));
        await harness.CreateRunAsync(definition);
        await harness.TickAsync();

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => harness.ClaimAsync($"worker-{i}")));

        claims.Count(c => c is not null).Should().Be(1);
    }

    [Fact]
    public async Task Cancelling_a_run_cooperatively_cancels_the_in_flight_step()
    {
        await using var harness = new EngineHarness(worker => worker.HeartbeatInterval = TimeSpan.FromMilliseconds(50));
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(
            Specs.Step("wait", handler: "delay", config: """{"milliseconds":30000}""", timeoutSeconds: 60)));
        var runId = await harness.CreateRunAsync(definition);
        await harness.TickAsync();
        var claim = await harness.ClaimAsync();

        var execution = harness.ExecuteAsync(claim!);
        await Task.Delay(100);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRunService>().CancelAsync(runId, CancellationToken.None);
        }

        var outcome = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        outcome.Should().Be(StepExecutionOutcome.Cancelled);
        var run = await harness.GetRunAsync(runId);
        run.Status.Should().Be(WorkflowRunStatus.Cancelled);
        run.Steps.Single().Status.Should().Be(StepRunStatus.Cancelled);
    }

    [Fact]
    public async Task Outputs_flow_to_downstream_steps_through_templates()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(
            Specs.Step("normalise", handler: "transform", config: """{"source":{"id":"{{input.order.id}}","qty":"{{input.order.qty}}"}}"""),
            Specs.Step("label", handler: "transform", dependsOn: new[] { "normalise" },
                config: """{"source":{"label":"order {{steps.normalise.output.id}} x{{steps.normalise.output.qty}}","qty":"{{steps.normalise.output.qty}}"}}""")));
        var runId = await harness.CreateRunAsync(definition, """{"order":{"id":"o-1","qty":3}}""");

        var run = await harness.DriveAsync(runId);

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        run.OutputJson.Should().Be("""{"label":{"label":"order o-1 x3","qty":3}}""");
    }

    [Fact]
    public async Task Max_concurrent_runs_keeps_extra_runs_pending()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(WorkflowDefinition.Create(
            "single-flight", 1, null, 1, new[] { Specs.Step("a", handler: "record") }, Specs.Now));
        var first = await harness.CreateRunAsync(definition);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await harness.CreateRunAsync(definition);

        var tick = await harness.TickAsync();

        tick.RunsStarted.Should().Be(1);
        (await harness.GetRunAsync(first)).Status.Should().Be(WorkflowRunStatus.Running);
        (await harness.GetRunAsync(second)).Status.Should().Be(WorkflowRunStatus.Pending);

        (await harness.DriveAsync(first)).Status.Should().Be(WorkflowRunStatus.Succeeded);
        (await harness.DriveAsync(second)).Status.Should().Be(WorkflowRunStatus.Succeeded);
    }

    [Fact]
    public async Task Due_schedule_materialises_exactly_one_run_per_slot()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(Specs.Step("a", handler: "record")));
        ScheduleDto schedule;
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            schedule = await scope.ServiceProvider.GetRequiredService<IScheduleService>().CreateAsync(
                definition.Id,
                new CreateScheduleRequest { IntervalSeconds = 60, Input = JsonDocument.Parse("""{"batch":1}""").RootElement.Clone() },
                CancellationToken.None);
        }

        (await harness.TickAsync()).SchedulesFired.Should().Be(0);
        harness.Clock.Advance(TimeSpan.FromSeconds(61));

        var tick = await harness.TickAsync();
        var again = await harness.TickAsync();

        tick.SchedulesFired.Should().Be(1);
        tick.RunsStarted.Should().Be(1);
        again.SchedulesFired.Should().Be(0);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var runs = await scope.ServiceProvider.GetRequiredService<IRunService>()
                .ListAsync(new RunFilter(null, definition.Id), PageRequest.Normalize(1, 10), CancellationToken.None);
            runs.TotalCount.Should().Be(1);
            runs.Items.Single().IdempotencyKey.Should().StartWith($"schedule:{schedule.Id:N}:");
        }
    }

    [Fact]
    public async Task Idempotent_run_creation_replays_or_conflicts()
    {
        await using var harness = new EngineHarness();
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(Specs.Step("a", handler: "record")));

        var first = await harness.CreateRunAsync(definition, """{"a":1,"b":2}""", "key-1");
        var replay = await harness.CreateRunAsync(definition, """{ "b": 2, "a": 1 }""", "key-1");
        var conflict = () => harness.CreateRunAsync(definition, """{"a":99}""", "key-1");

        replay.Should().Be(first, "semantically identical payloads replay the original run");
        await conflict.Should().ThrowAsync<ConflictException>();
        (await harness.CreateRunAsync(definition, """{"a":99}""", "key-2")).Should().NotBe(first);
    }

    [Fact]
    public async Task Idempotency_cache_outage_falls_back_to_the_database()
    {
        var broken = Substitute.For<IIdempotencyStore>();
        broken.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("redis down"));
        broken.SetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IdempotencyRecord>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("redis down"));
        await using var harness = new EngineHarness(configureServices: services => services.AddSingleton(broken));
        var definition = await harness.SaveDefinitionAsync(Specs.Definition(Specs.Step("a", handler: "record")));

        var first = await harness.CreateRunAsync(definition, """{"a":1}""", "key-1");
        var replay = await harness.CreateRunAsync(definition, """{"a":1}""", "key-1");

        replay.Should().Be(first);
        await broken.Received().GetAsync(Arg.Any<Guid>(), Arg.Is("key-1"), Arg.Any<CancellationToken>());
    }
}
