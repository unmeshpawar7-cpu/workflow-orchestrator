using System.Text.Json;
using FluentAssertions;
using Orchestrator.Application.Common;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Services;
using Orchestrator.Application.Validation;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.UnitTests.Application;

/// <summary>Keeps samples/ honest: every sample definition must pass the same validation as the API.</summary>
public sealed class SampleDefinitionTests
{
    private static readonly string[] BuiltInHandlers = { "http", "delay", "log", "transform", "fault-injection" };

    public static IEnumerable<object[]> SampleFiles()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "samples", "workflows");
        return Directory.GetFiles(directory, "*.json").OrderBy(f => f).Select(f => new object[] { Path.GetFileName(f) });
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public async Task Sample_definition_is_valid(string fileName)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "samples", "workflows", fileName));
        var request = JsonSerializer.Deserialize<CreateWorkflowDefinitionRequest>(json, JsonText.Web)!;

        await new CreateWorkflowDefinitionRequestValidator().EnsureValidAsync(request, CancellationToken.None);
        var specs = request.Steps.Select(WorkflowDefinitionService.ToSpec).ToList();
        var graph = WorkflowGraph.Validate(specs.Select(s => s.ToGraphNode()).ToList());

        graph.Errors.Should().BeEmpty();
        request.Steps.Select(s => s.Handler).Should().OnlyContain(h => BuiltInHandlers.Contains(h));
        // Not every sample has compensation steps, and OnlyContain fails on an empty collection,
        // so assert that no compensation handler is unknown instead.
        request.Steps.Where(s => s.Compensation is not null).Select(s => s.Compensation!.Handler)
            .Where(h => !BuiltInHandlers.Contains(h))
            .Should().BeEmpty("every compensation handler must be a built-in handler");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orchestrator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (Orchestrator.sln) not found.");
    }
}
