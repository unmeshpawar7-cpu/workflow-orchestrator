using FluentAssertions;
using Orchestrator.Domain.Common;
using Orchestrator.Domain.Workflows;
using Orchestrator.UnitTests.TestSupport;

namespace Orchestrator.UnitTests.Domain;

public sealed class WorkflowGraphTests
{
    private static GraphNode Node(string key, params string[] dependsOn) => new(key, dependsOn);

    [Fact]
    public void Linear_chain_is_valid_and_sorted_in_dependency_order()
    {
        var result = WorkflowGraph.Validate(new[] { Node("c", "b"), Node("a"), Node("b", "a") });

        result.IsValid.Should().BeTrue();
        result.TopologicalOrder.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Diamond_order_is_deterministic_and_respects_declaration_order_for_ties()
    {
        var result = WorkflowGraph.Validate(new[]
        {
            Node("start"),
            Node("right", "start"),
            Node("left", "start"),
            Node("join", "left", "right"),
        });

        result.IsValid.Should().BeTrue();
        result.TopologicalOrder.Should().Equal("start", "right", "left", "join");
    }

    [Fact]
    public void Cycle_is_detected_and_reported_as_a_path_in_execution_order()
    {
        var result = WorkflowGraph.Validate(new[] { Node("a", "c"), Node("b", "a"), Node("c", "b"), Node("d") });

        result.IsValid.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle(e => e.Code == GraphErrorCodes.Cycle).Subject;
        error.Cycle.Should().NotBeNull();
        var cycle = error.Cycle!;
        cycle.First().Should().Be(cycle.Last());
        cycle.Should().HaveCount(4).And.Contain(new[] { "a", "b", "c" });
        error.Message.Should().StartWith("Cycle detected: ");

        // Every consecutive pair must be a real "runs before" edge: X -> Y means Y depends on X.
        var dependsOn = new Dictionary<string, string> { ["a"] = "c", ["b"] = "a", ["c"] = "b" };
        for (var i = 0; i < cycle.Count - 1; i++)
        {
            dependsOn[cycle[i + 1]].Should().Be(cycle[i]);
        }

        result.TopologicalOrder.Should().BeEmpty();
    }

    [Fact]
    public void Cycle_hidden_behind_valid_prefix_is_found()
    {
        var result = WorkflowGraph.Validate(new[] { Node("root"), Node("x", "root", "y"), Node("y", "x") });

        result.Errors.Should().ContainSingle(e => e.Code == GraphErrorCodes.Cycle)
            .Which.Message.Should().ContainAll("x", "y");
    }

    [Fact]
    public void Unknown_and_self_dependencies_are_reported()
    {
        var result = WorkflowGraph.Validate(new[] { Node("a", "a"), Node("b", "ghost") });

        result.Errors.Select(e => e.Code).Should().BeEquivalentTo(new[] { GraphErrorCodes.SelfDependency, GraphErrorCodes.UnknownDependency });
        result.Errors.Single(e => e.Code == GraphErrorCodes.UnknownDependency).Message.Should().Contain("ghost");
    }

    [Fact]
    public void Duplicate_keys_are_reported_once()
    {
        var result = WorkflowGraph.Validate(new[] { Node("a"), Node("a"), Node("a") });

        result.Errors.Should().ContainSingle(e => e.Code == GraphErrorCodes.DuplicateKey && e.StepKey == "a");
    }

    [Fact]
    public void Too_many_steps_and_invalid_keys_are_rejected()
    {
        var nodes = Enumerable.Range(0, 4).Select(i => Node($"s{i}")).Append(Node("bad key!")).ToList();

        var result = WorkflowGraph.Validate(nodes, maxSteps: 3);

        result.Errors.Select(e => e.Code).Should().Contain(new[] { GraphErrorCodes.TooManySteps, GraphErrorCodes.InvalidKey });
    }

    [Fact]
    public void Empty_workflow_is_rejected()
    {
        WorkflowGraph.Validate(Array.Empty<GraphNode>()).Errors.Should().ContainSingle(e => e.Code == GraphErrorCodes.EmptyWorkflow);
    }

    [Fact]
    public void Templates_may_only_reference_upstream_steps()
    {
        var result = WorkflowGraph.Validate(new[]
        {
            new GraphNode("a", Array.Empty<string>()),
            new GraphNode("b", Array.Empty<string>(), """{"x":"{{steps.a.output.id}}"}"""),
            new GraphNode("c", new[] { "a" }, """{"x":"{{steps.a.output.id}}","y":"{{input.order.id}}"}"""),
            new GraphNode("d", new[] { "c" }, """{"x":"{{steps.a.output}}","z":"{{steps.nope.output}}"}"""),
        });

        result.Errors.Should().HaveCount(2);
        result.Errors.Should().Contain(e => e.Code == GraphErrorCodes.TemplateReferenceNotUpstream && e.StepKey == "b");
        result.Errors.Should().Contain(e => e.Code == GraphErrorCodes.UnknownTemplateReference && e.StepKey == "d");
    }

    [Fact]
    public void Compensation_config_may_reference_its_own_output_but_not_downstream_steps()
    {
        var valid = WorkflowGraph.Validate(new[]
        {
            new GraphNode("reserve", Array.Empty<string>(), null, """{"id":"{{steps.reserve.output.reservationId}}"}"""),
        });
        var invalid = WorkflowGraph.Validate(new[]
        {
            new GraphNode("reserve", Array.Empty<string>(), null, """{"id":"{{steps.ship.output.id}}"}"""),
            new GraphNode("ship", new[] { "reserve" }),
        });

        valid.IsValid.Should().BeTrue();
        invalid.Errors.Should().ContainSingle(e => e.Code == GraphErrorCodes.TemplateReferenceNotUpstream);
    }

    [Theory]
    [InlineData("{{ secrets.key }}")]
    [InlineData("{{steps.a}}")]
    [InlineData("{{steps.a.result}}")]
    [InlineData("{{input.a b}}")]
    public void Malformed_templates_are_rejected(string template)
    {
        var result = WorkflowGraph.Validate(new[]
        {
            new GraphNode("a", Array.Empty<string>()),
            new GraphNode("b", new[] { "a" }, $$"""{"x":"{{template}}"}"""),
        });

        result.Errors.Should().ContainSingle(e => e.Code == GraphErrorCodes.InvalidTemplate);
    }

    [Fact]
    public void Descendants_are_transitive_and_exclude_roots()
    {
        var nodes = new[] { Node("a"), Node("b", "a"), Node("c", "b"), Node("d"), Node("e", "d", "c") };

        WorkflowGraph.GetDescendants(nodes, new[] { "b" }).Should().BeEquivalentTo(new[] { "c", "e" });
        WorkflowGraph.GetDescendants(nodes, new[] { "e" }).Should().BeEmpty();
    }

    [Fact]
    public void Definition_factory_assigns_topological_ordinals_and_rejects_invalid_graphs()
    {
        var definition = Specs.Definition(
            Specs.Step("c", dependsOn: new[] { "b" }),
            Specs.Step("b", dependsOn: new[] { "a" }),
            Specs.Step("a"));

        definition.Steps.OrderBy(s => s.Ordinal).Select(s => s.Key).Should().Equal("a", "b", "c");
        definition.IsSink("c").Should().BeTrue();
        definition.IsSink("a").Should().BeFalse();

        var act = () => Specs.Definition(Specs.Step("a", dependsOn: new[] { "b" }), Specs.Step("b", dependsOn: new[] { "a" }));
        act.Should().Throw<DomainException>().WithMessage("*Cycle detected*");
    }
}
