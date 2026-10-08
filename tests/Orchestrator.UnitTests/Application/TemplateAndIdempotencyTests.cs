using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Orchestrator.Application.Common;
using Orchestrator.Application.Contracts;
using Orchestrator.Application.Engine;
using Orchestrator.Application.Services;
using Orchestrator.Application.Validation;
using Orchestrator.Domain.Workflows;

namespace Orchestrator.UnitTests.Application;

public sealed class TemplateResolverTests
{
    private readonly TemplateResolver _resolver = new();

    private static TemplateContext Context() =>
        new(
            """{"order":{"id":"ord-42","total":99.5,"lines":[{"sku":"A"},{"sku":"B"}]}}""",
            new Dictionary<string, string?>
            {
                ["reserve"] = """{"reservationId":"res-7","count":2,"ok":true}""",
                ["empty"] = null,
            });

    [Fact]
    public void Whole_string_placeholder_is_replaced_by_the_typed_json_value()
    {
        var resolved = _resolver.Resolve(
            """{"count":"{{steps.reserve.output.count}}","ok":"{{steps.reserve.output.ok}}","order":"{{input.order}}"}""",
            Context());

        var node = JsonNode.Parse(resolved!)!;
        node["count"]!.GetValue<int>().Should().Be(2);
        node["ok"]!.GetValue<bool>().Should().BeTrue();
        node["order"]!["id"]!.GetValue<string>().Should().Be("ord-42");
    }

    [Fact]
    public void Embedded_placeholders_are_interpolated_as_text()
    {
        var resolved = _resolver.Resolve(
            """{"url":"https://inventory.test/reservations/{{steps.reserve.output.reservationId}}?order={{ input.order.id }}&total={{input.order.total}}"}""",
            Context());

        JsonNode.Parse(resolved!)!["url"]!.GetValue<string>()
            .Should().Be("https://inventory.test/reservations/res-7?order=ord-42&total=99.5");
    }

    [Fact]
    public void Arrays_nested_objects_and_array_indices_are_supported()
    {
        var resolved = _resolver.Resolve(
            """{"items":["{{input.order.lines.1.sku}}",{"first":"{{input.order.lines.0}}"}],"n":3,"flag":null}""",
            Context());

        resolved.Should().Be("""{"items":["B",{"first":{"sku":"A"}}],"n":3,"flag":null}""");
    }

    [Fact]
    public void Configuration_without_templates_is_returned_unchanged()
    {
        _resolver.Resolve("""{"a":1,"b":"plain {text}"}""", Context()).Should().Be("""{"a":1,"b":"plain {text}"}""");
        _resolver.Resolve(null, Context()).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"x":"{{input.order.missing}}"}""", "did not match")]
    [InlineData("""{"x":"{{steps.unknown.output.id}}"}""", "has no output")]
    [InlineData("""{"x":"{{steps.empty.output}}"}""", "has no output")]
    [InlineData("""{"x":"{{input.order.lines.9}}"}""", "did not match")]
    [InlineData("""{"x":"{{env.SECRET}}"}""", "unknown root")]
    public void Unresolvable_templates_fail_permanently(string config, string reason)
    {
        var act = () => _resolver.Resolve(config, Context());

        act.Should().Throw<TemplateResolutionException>()
            .Where(e => !e.Retryable)
            .WithMessage($"*{reason}*");
    }

    [Fact]
    public void Invalid_json_configuration_is_a_permanent_failure()
    {
        var act = () => _resolver.Resolve("{not json", Context());

        act.Should().Throw<TemplateResolutionException>();
    }
}

public sealed class IdempotencyTests
{
    [Fact]
    public void Canonicalization_ignores_key_order_and_whitespace()
    {
        var a = RequestHasher.Canonicalize("""{ "b": 1, "a": { "y": [1, 2], "x": null } }""");
        var b = RequestHasher.Canonicalize("""{"a":{"x":null,"y":[1,2]},"b":1}""");

        a.Should().Be(b).And.Be("""{"a":{"x":null,"y":[1,2]},"b":1}""");
    }

    [Fact]
    public void Array_order_and_values_are_significant()
    {
        RequestHasher.Canonicalize("[1,2]").Should().NotBe(RequestHasher.Canonicalize("[2,1]"));
        RequestHasher.Hash("def", """{"a":1}""").Should().NotBe(RequestHasher.Hash("def", """{"a":2}"""));
    }

    [Fact]
    public void Hash_is_stable_lowercase_sha256_hex()
    {
        var hash = RequestHasher.Hash("x", null, "y");

        hash.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        RequestHasher.Hash("x", null, "y").Should().Be(hash);
    }

    [Theory]
    [InlineData("abc", "abc", IdempotencyOutcome.Replay)]
    [InlineData("abc", "abd", IdempotencyOutcome.Conflict)]
    [InlineData("", "abc", IdempotencyOutcome.Conflict)]
    public void Same_key_same_payload_replays_and_different_payload_conflicts(string stored, string incoming, IdempotencyOutcome expected)
    {
        IdempotencyPolicy.Evaluate(stored, incoming).Should().Be(expected);
    }
}

public sealed class RequestValidationTests
{
    [Fact]
    public async Task Definition_request_shape_is_validated_with_camel_cased_field_paths()
    {
        var validator = new CreateWorkflowDefinitionRequestValidator();
        var request = new CreateWorkflowDefinitionRequest
        {
            Name = "",
            MaxConcurrentRuns = 0,
            Steps = new List<StepDefinitionRequest>
            {
                new()
                {
                    Key = "bad key",
                    Handler = "log",
                    TimeoutSeconds = 0,
                    Config = JsonDocument.Parse("[1]").RootElement.Clone(),
                    Retry = new RetryPolicyRequest { MaxAttempts = 0, InitialDelayMs = 5000, MaxDelayMs = 10 },
                },
            },
        };

        var act = () => validator.EnsureValidAsync(request, CancellationToken.None);

        var errors = (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Select(e => e.Field).ToList();
        errors.Should().Contain(new[] { "name", "maxConcurrentRuns", "steps[0].key", "steps[0].timeoutSeconds", "steps[0].config", "steps[0].retry.maxAttempts" });
    }

    [Fact]
    public void Request_is_mapped_to_a_domain_spec_with_retry_defaults()
    {
        var spec = WorkflowDefinitionService.ToSpec(new StepDefinitionRequest
        {
            Key = "charge",
            Handler = "http",
            Config = JsonDocument.Parse("""{"url":"https://pay.test"}""").RootElement.Clone(),
            DependsOn = new List<string> { "reserve" },
            Compensation = new CompensationRequest { Handler = "http", Config = JsonDocument.Parse("""{"url":"https://pay.test/refund"}""").RootElement.Clone() },
        });

        spec.Retry.Should().Be(new RetrySettings(3, TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromMinutes(1), 0.2));
        spec.ConfigJson.Should().Be("""{"url":"https://pay.test"}""");
        spec.CompensationHandlerType.Should().Be("http");
        spec.DependsOn.Should().Equal("reserve");
    }
}
