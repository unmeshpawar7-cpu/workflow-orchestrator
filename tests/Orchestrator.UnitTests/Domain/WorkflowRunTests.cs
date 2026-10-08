using FluentAssertions;
using Orchestrator.Domain.Common;
using Orchestrator.Domain.Runs;
using Orchestrator.Domain.Workflows;
using Orchestrator.UnitTests.TestSupport;

namespace Orchestrator.UnitTests.Domain;

public sealed class WorkflowRunTests
{
    private static readonly DateTime Now = Specs.Now;

    private static WorkflowRun StartedRun(WorkflowDefinition definition)
    {
        var run = WorkflowRun.Create(definition, "{}", "corr-1", Now, Now);
        run.Start(definition, Now);
        return run;
    }

    private static void Succeed(WorkflowRun run, WorkflowDefinition definition, string key, string output = "{}")
    {
        run.GetStep(key).ClaimForExecution("worker", Now.AddMinutes(1), Now);
        run.RecordStepSucceeded(definition, key, output, 10, Now);
    }

    private static void FailPermanently(WorkflowRun run, WorkflowDefinition definition, string key)
    {
        run.GetStep(key).ClaimForExecution("worker", Now.AddMinutes(1), Now);
        run.RecordStepFailed(definition, key, "boom", retryAt: null, durationMs: 5, Now);
    }

    private static void CompensateNext(WorkflowRun run, WorkflowDefinition definition, string expectedKey)
    {
        var step = run.Steps.Single(s => s.Status == StepRunStatus.Compensating);
        step.StepKey.Should().Be(expectedKey);
        step.ClaimForCompensation("worker", Now.AddMinutes(1));
        run.RecordCompensationSucceeded(definition, expectedKey, Now);
    }

    [Fact]
    public void Start_promotes_only_steps_without_dependencies()
    {
        var definition = Specs.LinearWithCompensation();
        var run = StartedRun(definition);

        run.Status.Should().Be(WorkflowRunStatus.Running);
        run.GetStep("a").Status.Should().Be(StepRunStatus.Ready);
        run.GetStep("b").Status.Should().Be(StepRunStatus.Pending);
        run.DequeueEvents().Select(e => e.EventType).Should().Equal(
            ExecutionEventType.RunCreated, ExecutionEventType.RunStarted, ExecutionEventType.StepReady);
    }

    [Fact]
    public void Success_unlocks_dependents_and_completes_the_run_with_sink_outputs()
    {
        var definition = Specs.LinearWithCompensation();
        var run = StartedRun(definition);

        Succeed(run, definition, "a");
        run.GetStep("b").Status.Should().Be(StepRunStatus.Ready);
        Succeed(run, definition, "b");
        Succeed(run, definition, "c", """{"shipped":true}""");

        run.Status.Should().Be(WorkflowRunStatus.Succeeded);
        run.CompletedAt.Should().Be(Now);
        run.OutputJson.Should().Be("""{"c":{"shipped":true}}""");
    }

    [Fact]
    public void Fan_in_step_waits_for_all_dependencies()
    {
        var definition = Specs.Definition(
            Specs.Step("a"),
            Specs.Step("b"),
            Specs.Step("join", dependsOn: new[] { "a", "b" }));
        var run = StartedRun(definition);

        Succeed(run, definition, "a");
        run.GetStep("join").Status.Should().Be(StepRunStatus.Pending);
        Succeed(run, definition, "b");
        run.GetStep("join").Status.Should().Be(StepRunStatus.Ready);
    }

    [Fact]
    public void Retryable_failure_schedules_a_retry_and_keeps_the_run_running()
    {
        var definition = Specs.LinearWithCompensation();
        var run = StartedRun(definition);
        run.GetStep("a").ClaimForExecution("worker", Now.AddMinutes(1), Now);

        run.RecordStepFailed(definition, "a", "transient", Now.AddSeconds(2), 5, Now);

        var step = run.GetStep("a");
        step.Status.Should().Be(StepRunStatus.Retrying);
        step.NextAttemptAt.Should().Be(Now.AddSeconds(2));
        step.LeaseOwner.Should().BeNull();
        run.Status.Should().Be(WorkflowRunStatus.Running);
    }

    [Fact]
    public void Compensation_runs_in_reverse_topological_order_then_the_run_is_compensated()
    {
        var definition = Specs.Definition(
            Specs.Step("a", compensation: "log"),
            Specs.Step("b", dependsOn: new[] { "a" }, compensation: "log"),
            Specs.Step("no-undo", dependsOn: new[] { "b" }),
            Specs.Step("d", dependsOn: new[] { "no-undo" }));
        var run = StartedRun(definition);
        Succeed(run, definition, "a");
        Succeed(run, definition, "b");
        Succeed(run, definition, "no-undo");

        FailPermanently(run, definition, "d");

        run.Status.Should().Be(WorkflowRunStatus.Compensating);
        CompensateNext(run, definition, "b");
        CompensateNext(run, definition, "a");
        run.Status.Should().Be(WorkflowRunStatus.Compensated);
        run.GetStep("no-undo").Status.Should().Be(StepRunStatus.Succeeded, "steps without a compensation handler are left as they are");
        run.DequeueEvents().Select(e => e.EventType).Should().ContainInOrder(
            ExecutionEventType.StepFailed,
            ExecutionEventType.RunFailed,
            ExecutionEventType.CompensationStarted,
            ExecutionEventType.StepCompensated,
            ExecutionEventType.StepCompensated,
            ExecutionEventType.RunCompensated);
    }

    [Fact]
    public void Failure_skips_waiting_steps_in_other_branches()
    {
        var definition = Specs.Definition(Specs.Step("a"), Specs.Step("b"), Specs.Step("c", dependsOn: new[] { "b" }));
        var run = StartedRun(definition);

        FailPermanently(run, definition, "a");

        run.Status.Should().Be(WorkflowRunStatus.Failed, "nothing succeeded, so there is nothing to compensate");
        run.GetStep("b").Status.Should().Be(StepRunStatus.Skipped);
        run.GetStep("c").Status.Should().Be(StepRunStatus.Skipped);
        run.Error.Should().Contain("Step 'a' failed");
    }

    [Fact]
    public void Compensation_waits_for_in_flight_steps_and_also_compensates_late_successes()
    {
        var definition = Specs.Definition(
            Specs.Step("a", compensation: "log"),
            Specs.Step("b", compensation: "log"),
            Specs.Step("c"));
        var run = StartedRun(definition);
        Succeed(run, definition, "a");
        run.GetStep("b").ClaimForExecution("worker-2", Now.AddMinutes(1), Now);

        FailPermanently(run, definition, "c");

        run.Status.Should().Be(WorkflowRunStatus.Compensating);
        run.GetStep("a").Status.Should().Be(StepRunStatus.Succeeded, "compensation waits while a sibling attempt is in flight");

        run.RecordStepSucceeded(definition, "b", "{}", 10, Now);

        run.GetStep("b").Status.Should().Be(StepRunStatus.Compensating, "the late success is compensated first (higher ordinal)");
        run.GetStep("a").Status.Should().Be(StepRunStatus.Succeeded);
    }

    [Fact]
    public void Failed_compensation_dead_letters_the_run_and_requeue_resumes_it()
    {
        var definition = Specs.LinearWithCompensation();
        var run = StartedRun(definition);
        Succeed(run, definition, "a");
        FailPermanently(run, definition, "b");
        run.GetStep("a").ClaimForCompensation("worker", Now.AddMinutes(1));

        run.RecordCompensationFailed(definition, "a", "downstream unavailable", retryAt: null, Now);

        run.Status.Should().Be(WorkflowRunStatus.DeadLettered);
        run.GetStep("a").Status.Should().Be(StepRunStatus.CompensationFailed);
        run.Error.Should().Contain("Compensation of step 'a' failed");

        run.RequeueCompensation(definition, Now);

        run.Status.Should().Be(WorkflowRunStatus.Compensating);
        run.GetStep("a").Status.Should().Be(StepRunStatus.Compensating);
        run.GetStep("a").CompensationAttempt.Should().Be(0);
        CompensateNext(run, definition, "a");
        run.Status.Should().Be(WorkflowRunStatus.Compensated);
    }

    [Fact]
    public void Retry_resets_failed_steps_and_their_descendants_but_keeps_successes()
    {
        var definition = Specs.Definition(Specs.Step("a"), Specs.Step("b", dependsOn: new[] { "a" }), Specs.Step("c", dependsOn: new[] { "b" }));
        var run = StartedRun(definition);
        Succeed(run, definition, "a");
        FailPermanently(run, definition, "b");
        run.Status.Should().Be(WorkflowRunStatus.Failed);

        run.Retry(definition, Now);

        run.Status.Should().Be(WorkflowRunStatus.Running);
        run.Error.Should().BeNull();
        run.GetStep("a").Status.Should().Be(StepRunStatus.Succeeded);
        run.GetStep("b").Status.Should().Be(StepRunStatus.Ready);
        run.GetStep("b").Attempt.Should().Be(0);
        run.GetStep("c").Status.Should().Be(StepRunStatus.Pending);
    }

    [Fact]
    public void Retry_of_a_dead_lettered_run_re_executes_compensated_steps()
    {
        var definition = Specs.LinearWithCompensation();
        var run = StartedRun(definition);
        Succeed(run, definition, "a");
        Succeed(run, definition, "b");
        FailPermanently(run, definition, "c");
        CompensateNext(run, definition, "b");
        run.GetStep("a").ClaimForCompensation("worker", Now.AddMinutes(1));
        run.RecordCompensationFailed(definition, "a", "nope", null, Now);
        run.Status.Should().Be(WorkflowRunStatus.DeadLettered);

        run.Retry(definition, Now);

        run.Steps.Select(s => s.Status).Should().Equal(StepRunStatus.Ready, StepRunStatus.Pending, StepRunStatus.Pending);
    }

    [Fact]
    public void Cancel_cancels_waiting_steps_and_leaves_running_ones_for_cooperative_cancellation()
    {
        var definition = Specs.Definition(Specs.Step("a"), Specs.Step("b"), Specs.Step("c", dependsOn: new[] { "a" }));
        var run = StartedRun(definition);
        run.GetStep("a").ClaimForExecution("worker", Now.AddMinutes(1), Now);

        run.Cancel(Now, "operator request");

        run.Status.Should().Be(WorkflowRunStatus.Cancelled);
        run.GetStep("a").Status.Should().Be(StepRunStatus.Running);
        run.GetStep("b").Status.Should().Be(StepRunStatus.Cancelled);
        run.GetStep("c").Status.Should().Be(StepRunStatus.Cancelled);

        run.RecordStepCancelled(definition, "a", "observed cancellation", Now);
        run.GetStep("a").Status.Should().Be(StepRunStatus.Cancelled);
    }

    [Fact]
    public void Illegal_operations_throw_domain_exceptions()
    {
        var definition = Specs.Definition(Specs.Step("a"));
        var run = StartedRun(definition);
        Succeed(run, definition, "a");

        run.Invoking(r => r.Cancel(Now)).Should().Throw<InvalidStateTransitionException>();
        run.Invoking(r => r.Retry(definition, Now)).Should().Throw<InvalidStateTransitionException>();
        run.Invoking(r => r.RequeueCompensation(definition, Now)).Should().Throw<InvalidStateTransitionException>();
        run.Invoking(r => r.RecordStepSucceeded(definition, "a", null, 1, Now)).Should().Throw<InvalidStateTransitionException>();

        var other = Specs.Definition(Specs.Step("a"));
        run.Invoking(r => r.Reconcile(other, Now)).Should().Throw<DomainException>().WithMessage("*does not belong*");
    }

    [Fact]
    public void Expired_lease_on_a_running_step_becomes_a_retry_or_a_failure()
    {
        var definition = Specs.Definition(Specs.Step("a"), Specs.Step("b"));
        var run = StartedRun(definition);
        run.GetStep("a").ClaimForExecution("dead-worker", Now.AddSeconds(30), Now);
        run.GetStep("b").ClaimForExecution("dead-worker", Now.AddSeconds(30), Now);

        run.RecoverExpiredLease(definition, "a", Now.AddSeconds(1), Now.AddMinutes(1)).Should().BeTrue();
        run.GetStep("a").Status.Should().Be(StepRunStatus.Retrying);
        run.GetStep("a").LastError.Should().Contain("Lease expired");

        run.RecoverExpiredLease(definition, "b", retryAt: null, Now.AddMinutes(1)).Should().BeTrue();
        run.GetStep("b").Status.Should().Be(StepRunStatus.Failed);
        run.Status.Should().Be(WorkflowRunStatus.Failed);

        run.RecoverExpiredLease(definition, "b", null, Now).Should().BeFalse("nothing is leased any more");
    }

    [Fact]
    public void Every_mutation_changes_the_concurrency_version()
    {
        var definition = Specs.Definition(Specs.Step("a"));
        var run = WorkflowRun.Create(definition, "{}", "c", Now, Now);
        var versions = new List<Guid> { run.Version };

        run.Start(definition, Now);
        versions.Add(run.Version);
        run.GetStep("a").ClaimForExecution("w", Now, Now);
        run.RecordStepSucceeded(definition, "a", null, 1, Now);
        versions.Add(run.Version);

        versions.Should().OnlyHaveUniqueItems();
    }
}
