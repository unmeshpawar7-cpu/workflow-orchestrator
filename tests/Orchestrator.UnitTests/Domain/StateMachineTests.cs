using FluentAssertions;
using Orchestrator.Domain.Common;
using Orchestrator.Domain.Runs;

namespace Orchestrator.UnitTests.Domain;

public sealed class StateMachineTests
{
    private static readonly HashSet<(WorkflowRunStatus, WorkflowRunStatus)> ExpectedRunTransitions = new()
    {
        (WorkflowRunStatus.Pending, WorkflowRunStatus.Running),
        (WorkflowRunStatus.Pending, WorkflowRunStatus.Cancelled),
        (WorkflowRunStatus.Running, WorkflowRunStatus.Succeeded),
        (WorkflowRunStatus.Running, WorkflowRunStatus.Failed),
        (WorkflowRunStatus.Running, WorkflowRunStatus.Cancelled),
        (WorkflowRunStatus.Failed, WorkflowRunStatus.Compensating),
        (WorkflowRunStatus.Failed, WorkflowRunStatus.Running),
        (WorkflowRunStatus.Compensating, WorkflowRunStatus.Compensated),
        (WorkflowRunStatus.Compensating, WorkflowRunStatus.DeadLettered),
        (WorkflowRunStatus.DeadLettered, WorkflowRunStatus.Compensating),
        (WorkflowRunStatus.DeadLettered, WorkflowRunStatus.Running),
    };

    private static readonly HashSet<(StepRunStatus, StepRunStatus)> ExpectedStepTransitions = new()
    {
        (StepRunStatus.Pending, StepRunStatus.Ready),
        (StepRunStatus.Pending, StepRunStatus.Skipped),
        (StepRunStatus.Pending, StepRunStatus.Cancelled),
        (StepRunStatus.Ready, StepRunStatus.Running),
        (StepRunStatus.Ready, StepRunStatus.Skipped),
        (StepRunStatus.Ready, StepRunStatus.Cancelled),
        (StepRunStatus.Running, StepRunStatus.Succeeded),
        (StepRunStatus.Running, StepRunStatus.Failed),
        (StepRunStatus.Running, StepRunStatus.Retrying),
        (StepRunStatus.Running, StepRunStatus.Cancelled),
        (StepRunStatus.Retrying, StepRunStatus.Running),
        (StepRunStatus.Retrying, StepRunStatus.Skipped),
        (StepRunStatus.Retrying, StepRunStatus.Cancelled),
        (StepRunStatus.Succeeded, StepRunStatus.Compensating),
        (StepRunStatus.Succeeded, StepRunStatus.Pending),
        (StepRunStatus.Failed, StepRunStatus.Pending),
        (StepRunStatus.Skipped, StepRunStatus.Pending),
        (StepRunStatus.Cancelled, StepRunStatus.Pending),
        (StepRunStatus.Compensating, StepRunStatus.Compensated),
        (StepRunStatus.Compensating, StepRunStatus.CompensationFailed),
        (StepRunStatus.Compensated, StepRunStatus.Pending),
        (StepRunStatus.CompensationFailed, StepRunStatus.Compensating),
        (StepRunStatus.CompensationFailed, StepRunStatus.Pending),
    };

    public static IEnumerable<object[]> AllRunPairs() =>
        from source in Enum.GetValues<WorkflowRunStatus>()
        from target in Enum.GetValues<WorkflowRunStatus>()
        select new object[] { source, target };

    public static IEnumerable<object[]> AllStepPairs() =>
        from source in Enum.GetValues<StepRunStatus>()
        from target in Enum.GetValues<StepRunStatus>()
        select new object[] { source, target };

    [Theory]
    [MemberData(nameof(AllRunPairs))]
    public void Run_transition_matrix_matches_the_specification(WorkflowRunStatus from, WorkflowRunStatus to)
    {
        var expected = ExpectedRunTransitions.Contains((from, to));

        RunStateMachine.CanTransition(from, to).Should().Be(expected);
        var act = () => RunStateMachine.EnsureCanTransition(from, to);
        if (expected)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<InvalidStateTransitionException>().Which.To.Should().Be(to.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(AllStepPairs))]
    public void Step_transition_matrix_matches_the_specification(StepRunStatus from, StepRunStatus to)
    {
        var expected = ExpectedStepTransitions.Contains((from, to));

        StepStateMachine.CanTransition(from, to).Should().Be(expected);
        var act = () => StepStateMachine.EnsureCanTransition(from, to);
        if (expected)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<InvalidStateTransitionException>();
        }
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Succeeded)]
    [InlineData(WorkflowRunStatus.Compensated)]
    [InlineData(WorkflowRunStatus.Cancelled)]
    public void Terminal_run_states_have_no_exits(WorkflowRunStatus status)
    {
        RunStateMachine.IsTerminal(status).Should().BeTrue();
        RunStateMachine.AllowedTargets(status).Should().BeEmpty();
    }

    [Fact]
    public void Dead_lettered_is_not_terminal_because_operators_can_recover_it()
    {
        RunStateMachine.IsTerminal(WorkflowRunStatus.DeadLettered).Should().BeFalse();
    }
}
