using System.Net;
using System.Text.Json;
using FluentAssertions;
using Orchestrator.IntegrationTests.Infrastructure;

namespace Orchestrator.IntegrationTests;

internal static class Definitions
{
    public static object Simple(string name) => new
    {
        name,
        steps = new object[]
        {
            new { key = "greet", handler = "log", config = new { message = "hello {{input.who}}" } },
        },
    };
}

public sealed class AuthAndPlatformTests : IClassFixture<OrchestratorApiFactory>
{
    private readonly OrchestratorApiFactory _factory;

    public AuthAndPlatformTests(OrchestratorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Missing_or_unknown_api_key_is_rejected_with_401_envelope()
    {
        var anonymous = await _factory.CreateClient(apiKey: null).GetAsync("/api/workflows");
        var wrongKey = await _factory.CreateClient("not-a-real-key").GetAsync("/api/workflows");

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrongKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await anonymous.ReadJsonAsync();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("message").GetString().Should().Contain("X-Api-Key");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Roles_are_enforced_per_endpoint_with_403()
    {
        var reader = _factory.CreateClient(OrchestratorApiFactory.ReaderKey);
        var operatorClient = _factory.CreateClient(OrchestratorApiFactory.OperatorKey);

        (await reader.GetAsync("/api/workflows")).StatusCode.Should().Be(HttpStatusCode.OK);
        var readerCreate = await reader.PostJsonAsync("/api/workflows", Definitions.Simple("reader-attempt"));
        var operatorCreate = await operatorClient.PostJsonAsync("/api/workflows", Definitions.Simple("operator-attempt"));

        readerCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        operatorCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await readerCreate.ReadJsonAsync()).GetProperty("success").GetBoolean().Should().BeFalse();

        var workflowId = await _factory.CreateClient(OrchestratorApiFactory.AdminKey).CreateWorkflowAsync(Definitions.Simple("rbac-demo"));
        (await reader.PostJsonAsync($"/api/workflows/{workflowId}/runs", new { input = new { } })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "readers cannot start runs");
    }

    [Fact]
    public async Task Health_endpoints_are_anonymous()
    {
        var client = _factory.CreateClient(apiKey: null);

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ready.ReadJsonAsync()).GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).Should().Contain("database");
    }

    [Fact]
    public async Task Correlation_id_is_echoed_and_used_as_trace_id()
    {
        var client = _factory.CreateClient(apiKey: null);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/runs");
        request.Headers.Add("X-Correlation-Id", "it-corr-001");

        var response = await client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle().Which.Should().Be("it-corr-001");
        (await response.ReadJsonAsync()).GetProperty("traceId").GetString().Should().Be("it-corr-001");
    }

    [Fact]
    public async Task OpenApi_document_is_generated()
    {
        var response = await _factory.CreateClient(apiKey: null).GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await response.ReadJsonAsync();
        document.GetProperty("paths").EnumerateObject().Select(p => p.Name)
            .Should().Contain(new[] { "/api/workflows", "/api/workflows/{id}/runs", "/api/runs/{id}/history", "/api/dead-letters/{runId}/requeue" });
    }
}

public sealed class WorkflowApiTests : IClassFixture<OrchestratorApiFactory>
{
    private readonly HttpClient _admin;

    public WorkflowApiTests(OrchestratorApiFactory factory)
    {
        _admin = factory.CreateClient(OrchestratorApiFactory.AdminKey);
    }

    [Fact]
    public async Task Creating_a_definition_returns_201_with_location_and_reusing_the_name_creates_a_new_version()
    {
        var first = await _admin.PostJsonAsync("/api/workflows", Definitions.Simple("versioned"));
        var second = await _admin.PostJsonAsync("/api/workflows", Definitions.Simple("versioned"));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var data = (await first.ReadJsonAsync()).GetProperty("data");
        first.Headers.Location!.ToString().Should().EndWith($"/api/workflows/{data.GetProperty("id").GetGuid()}");
        data.GetProperty("version").GetInt32().Should().Be(1);
        data.GetProperty("steps")[0].GetProperty("retry").GetProperty("maxAttempts").GetInt32().Should().Be(3);
        (await second.ReadJsonAsync()).GetProperty("data").GetProperty("version").GetInt32().Should().Be(2);

        var fetched = await _admin.GetAsync(first.Headers.Location);
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cyclic_definition_is_rejected_with_cycle_details()
    {
        var response = await _admin.PostJsonAsync("/api/workflows", new
        {
            name = "cyclic",
            steps = new object[]
            {
                new { key = "a", handler = "log", dependsOn = new[] { "c" } },
                new { key = "b", handler = "log", dependsOn = new[] { "a" } },
                new { key = "c", handler = "log", dependsOn = new[] { "b" } },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.ReadJsonAsync();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        var messages = body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("message").GetString()).ToList();
        messages.Should().ContainSingle(m => m!.StartsWith("Cycle detected: ") && m.Contains("a") && m.Contains("b") && m.Contains("c"));
    }

    [Fact]
    public async Task Unknown_handler_and_shape_errors_are_reported_per_field()
    {
        var response = await _admin.PostJsonAsync("/api/workflows", new
        {
            name = "bad-handlers",
            steps = new object[]
            {
                new { key = "a", handler = "teleport" },
                new { key = "b", handler = "log", timeoutSeconds = 0, dependsOn = new[] { "ghost" } },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var fields = (await response.ReadJsonAsync()).GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("field").GetString()).ToList();
        fields.Should().Contain("steps[1].timeoutSeconds");

        var graphResponse = await _admin.PostJsonAsync("/api/workflows", new
        {
            name = "bad-graph",
            steps = new object[] { new { key = "a", handler = "teleport" }, new { key = "b", handler = "log", dependsOn = new[] { "ghost" } } },
        });
        var graphFields = (await graphResponse.ReadJsonAsync()).GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("field").GetString()).ToList();
        graphFields.Should().Contain(new[] { "steps[0].handler", "steps.b" });
    }

    [Fact]
    public async Task Unknown_definition_returns_404_envelope()
    {
        var response = await _admin.GetAsync($"/api/workflows/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.ReadJsonAsync()).GetProperty("success").GetBoolean().Should().BeFalse();
    }
}

public sealed class RunApiTests : IClassFixture<OrchestratorApiFactory>
{
    private readonly OrchestratorApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _operator;

    public RunApiTests(OrchestratorApiFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClient(OrchestratorApiFactory.AdminKey);
        _operator = factory.CreateClient(OrchestratorApiFactory.OperatorKey);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_run_and_a_different_payload_conflicts()
    {
        var workflowId = await _admin.CreateWorkflowAsync(Definitions.Simple("idempotent"));
        var url = $"/api/workflows/{workflowId}/runs";

        var first = await _operator.PostJsonAsync(url, new { input = new { who = "ada", n = 1 } }, "order-123");
        var replay = await _operator.PostJsonAsync(url, new { input = new { n = 1, who = "ada" } }, "order-123");
        var conflict = await _operator.PostJsonAsync(url, new { input = new { who = "grace" } }, "order-123");

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        first.Headers.Location.Should().NotBeNull();
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var firstId = (await first.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();
        var replayBody = await replay.ReadJsonAsync();
        replayBody.GetProperty("data").GetProperty("id").GetGuid().Should().Be(firstId);
        replayBody.GetProperty("message").GetString().Should().Contain("Idempotent replay");
        first.Headers.Location!.ToString().Should().EndWith($"/api/runs/{firstId}");
    }

    [Fact]
    public async Task Run_executes_end_to_end_with_templated_step_outputs()
    {
        var workflowId = await _admin.CreateWorkflowAsync(new
        {
            name = "e2e",
            steps = new object[]
            {
                new { key = "normalise", handler = "transform", config = new { source = new { id = "{{input.orderId}}", qty = "{{input.qty}}" } } },
                new { key = "wait", handler = "delay", dependsOn = new[] { "normalise" }, config = new { milliseconds = 50 } },
                new { key = "announce", handler = "log", dependsOn = new[] { "wait" }, config = new { message = "order {{steps.normalise.output.id}} x{{steps.normalise.output.qty}}" } },
            },
        });

        var runId = await _operator.StartRunAsync(workflowId, new { orderId = "o-77", qty = 2 });
        var run = await _operator.WaitForStatusAsync(runId, "Succeeded");

        run.GetProperty("output").GetProperty("announce").GetProperty("message").GetString().Should().Be("order o-77 x2");
        run.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("status").GetString()).Should().OnlyContain(s => s == "Succeeded");
        run.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();

        var history = (await (await _operator.GetAsync($"/api/runs/{runId}/history?pageSize=100")).ReadJsonAsync()).GetProperty("data");
        var types = history.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList();
        types.First().Should().Be("RunCreated");
        types.Last().Should().Be("RunSucceeded");
        types.Count(t => t == "StepSucceeded").Should().Be(3);

        var stats = (await (await _operator.GetAsync("/api/stats")).ReadJsonAsync()).GetProperty("data");
        stats.GetProperty("runsByStatus").GetProperty("Succeeded").GetInt32().Should().BeGreaterThan(0);
        stats.GetProperty("averageStepDurationMs").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Failed_compensation_dead_letters_the_run_and_requeue_compensates_it()
    {
        _factory.Downstream.StatusCode = HttpStatusCode.ServiceUnavailable;
        var workflowId = await _admin.CreateWorkflowAsync(new
        {
            name = "dead-letter-flow",
            steps = new object[]
            {
                new
                {
                    key = "reserve",
                    handler = "log",
                    config = new { message = "reserved" },
                    retry = new { maxAttempts = 1, initialDelayMs = 0, maxDelayMs = 0 },
                    compensation = new { handler = "http", config = new { method = "POST", url = "http://inventory.test/release" } },
                },
                new { key = "charge", handler = "fault-injection", dependsOn = new[] { "reserve" }, config = new { retryable = false } },
            },
        });

        var runId = await _operator.StartRunAsync(workflowId);
        await _operator.WaitForStatusAsync(runId, "DeadLettered");

        var deadLetters = (await (await _operator.GetAsync($"/api/dead-letters?definitionId={workflowId}")).ReadJsonAsync()).GetProperty("data");
        deadLetters.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).Should().Contain(runId);

        _factory.Downstream.StatusCode = HttpStatusCode.OK;
        var requeue = await _operator.PostAsync($"/api/dead-letters/{runId}/requeue", content: null);
        requeue.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var run = await _operator.WaitForStatusAsync(runId, "Compensated");
        run.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "reserve")
            .GetProperty("status").GetString().Should().Be("Compensated");
        _factory.Downstream.Calls.Should().BeGreaterThanOrEqualTo(2);

        (await _operator.PostAsync($"/api/dead-letters/{runId}/requeue", content: null)).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "the run is no longer dead-lettered");
    }

    [Fact]
    public async Task Cancelling_a_running_run_stops_its_in_flight_step()
    {
        var workflowId = await _admin.CreateWorkflowAsync(new
        {
            name = "cancellable",
            steps = new object[] { new { key = "long", handler = "delay", timeoutSeconds = 120, config = new { milliseconds = 60000 } } },
        });
        var runId = await _operator.StartRunAsync(workflowId);
        await _operator.WaitForRunAsync(runId, data => data.GetProperty("steps")[0].GetProperty("status").GetString() == "Running");

        var cancel = await _operator.PostAsync($"/api/runs/{runId}/cancel", content: null);

        cancel.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await cancel.ReadJsonAsync()).GetProperty("data").GetProperty("status").GetString().Should().Be("Cancelled");
        await _operator.WaitForRunAsync(runId, data => data.GetProperty("steps")[0].GetProperty("status").GetString() == "Cancelled");
        (await _operator.PostAsync($"/api/runs/{runId}/cancel", content: null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Failed_run_can_be_retried_but_a_succeeded_run_cannot()
    {
        var failing = await _admin.CreateWorkflowAsync(new
        {
            name = "retry-me",
            steps = new object[] { new { key = "boom", handler = "fault-injection", config = new { retryable = false } } },
        });
        var runId = await _operator.StartRunAsync(failing);
        await _operator.WaitForStatusAsync(runId, "Failed");

        var retry = await _operator.PostAsync($"/api/runs/{runId}/retry", content: null);

        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await retry.ReadJsonAsync()).GetProperty("data").GetProperty("status").GetString().Should().Be("Running");

        var ok = await _operator.StartRunAsync(await _admin.CreateWorkflowAsync(Definitions.Simple("retry-ok")));
        await _operator.WaitForStatusAsync(ok, "Succeeded");
        (await _operator.PostAsync($"/api/runs/{ok}/retry", content: null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Run_listing_is_paginated_filtered_and_capped()
    {
        var workflowId = await _admin.CreateWorkflowAsync(Definitions.Simple("paged"));
        for (var i = 0; i < 3; i++)
        {
            await _operator.StartRunAsync(workflowId, new { who = $"n{i}" });
        }

        var page1 = (await (await _operator.GetAsync($"/api/runs?definitionId={workflowId}&page=1&pageSize=2")).ReadJsonAsync()).GetProperty("data");
        var page2 = (await (await _operator.GetAsync($"/api/runs?definitionId={workflowId}&page=2&pageSize=2")).ReadJsonAsync()).GetProperty("data");
        var capped = (await (await _operator.GetAsync("/api/runs?pageSize=1000")).ReadJsonAsync()).GetProperty("data");

        page1.GetProperty("items").GetArrayLength().Should().Be(2);
        page1.GetProperty("totalCount").GetInt32().Should().Be(3);
        page1.GetProperty("totalPages").GetInt32().Should().Be(2);
        page2.GetProperty("items").GetArrayLength().Should().Be(1);
        page2.GetProperty("page").GetInt32().Should().Be(2);
        capped.GetProperty("pageSize").GetInt32().Should().Be(100);

        (await _operator.GetAsync("/api/runs?status=NotAStatus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _operator.GetAsync($"/api/runs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Schedules_can_be_created_listed_and_disabled()
    {
        var workflowId = await _admin.CreateWorkflowAsync(Definitions.Simple("scheduled"));

        var created = await _operator.PostJsonAsync($"/api/workflows/{workflowId}/schedules", new { intervalSeconds = 3600, input = new { who = "cron" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var scheduleId = (await created.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/schedules/{scheduleId}") { Content = System.Net.Http.Json.JsonContent.Create(new { enabled = false }) };
        var disabled = await _operator.SendAsync(patch);

        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await disabled.ReadJsonAsync()).GetProperty("data").GetProperty("enabled").GetBoolean().Should().BeFalse();
        var list = (await (await _operator.GetAsync($"/api/schedules?definitionId={workflowId}")).ReadJsonAsync()).GetProperty("data");
        list.GetProperty("totalCount").GetInt32().Should().Be(1);

        var invalid = await _operator.PostJsonAsync($"/api/workflows/{workflowId}/schedules", new { enabled = true });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
