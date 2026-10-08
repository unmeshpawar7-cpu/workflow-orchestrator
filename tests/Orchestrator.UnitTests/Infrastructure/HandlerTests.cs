using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Application.Engine;
using Orchestrator.Infrastructure.Handlers;

namespace Orchestrator.UnitTests.Infrastructure;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<(HttpMethod Method, Uri? Uri, Dictionary<string, string> Headers, string? Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add((request.Method, request.RequestUri, headers, body));
        return _responder(request);
    }
}

internal static class HandlerContext
{
    public static StepExecutionContext For(string? config, int attempt = 1, bool compensation = false) =>
        new()
        {
            RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            StepKey = "step",
            Attempt = attempt,
            IsCompensation = compensation,
            CorrelationId = "corr-123",
            ConfigJson = config,
        };
}

public sealed class HttpStepHandlerTests
{
    private static HttpStepHandler Create(StubHttpMessageHandler stub, params string[] allowedHosts) =>
        new(new HttpClient(stub), Options.Create(new HttpHandlerOptions { AllowedHosts = allowedHosts.ToList() }));

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Sends_configured_request_and_returns_status_and_parsed_body()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created, """{"reservationId":"r-1"}"""));
        var handler = Create(stub);

        var result = await handler.ExecuteAsync(
            HandlerContext.For("""{"method":"post","url":"https://inventory.test/reservations","headers":{"X-Tenant":"acme"},"body":{"sku":"A","qty":2}}"""),
            CancellationToken.None);

        var output = JsonNode.Parse(result.OutputJson!)!;
        output["statusCode"]!.GetValue<int>().Should().Be(201);
        output["body"]!["reservationId"]!.GetValue<string>().Should().Be("r-1");
        var request = stub.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.ToString().Should().Be("https://inventory.test/reservations");
        request.Body.Should().Be("""{"sku":"A","qty":2}""");
        request.Headers["X-Tenant"].Should().Be("acme");
        request.Headers["X-Correlation-Id"].Should().Be("corr-123");
        request.Headers["Idempotency-Key"].Should().Be("11111111222233334444555555555555:step:execute");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Non_success_status_maps_to_retryable_or_permanent_failure(HttpStatusCode status, bool retryable)
    {
        var handler = Create(new StubHttpMessageHandler(_ => Json(status, "{}")));

        var act = () => handler.ExecuteAsync(HandlerContext.For("""{"url":"https://svc.test/x"}"""), CancellationToken.None);

        (await act.Should().ThrowAsync<StepFailedException>()).Which.Retryable.Should().Be(retryable);
    }

    [Fact]
    public async Task Custom_success_status_codes_are_honoured_and_non_json_bodies_are_kept_as_text()
    {
        var handler = Create(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("gone") }));

        var result = await handler.ExecuteAsync(HandlerContext.For("""{"url":"https://svc.test/x","successStatusCodes":[404]}"""), CancellationToken.None);

        JsonNode.Parse(result.OutputJson!)!["body"]!.GetValue<string>().Should().Be("gone");
    }

    [Theory]
    [InlineData("""{"url":"ftp://files.test/x"}""")]
    [InlineData("""{"url":"not a url"}""")]
    [InlineData("""{"url":"https://evil.test/x"}""")]
    public async Task Invalid_or_disallowed_urls_fail_permanently_without_a_request(string config)
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var handler = Create(stub, "svc.test");

        var act = () => handler.ExecuteAsync(HandlerContext.For(config), CancellationToken.None);

        (await act.Should().ThrowAsync<StepFailedException>()).Which.Retryable.Should().BeFalse();
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_configuration_is_a_permanent_failure()
    {
        var handler = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}")));

        var act = () => handler.ExecuteAsync(HandlerContext.For("""{"url": 42}"""), CancellationToken.None);

        (await act.Should().ThrowAsync<StepFailedException>()).Which.Retryable.Should().BeFalse();
    }
}

public sealed class SimpleHandlerTests
{
    [Fact]
    public async Task Delay_waits_and_reports_the_delay()
    {
        var result = await new DelayStepHandler().ExecuteAsync(HandlerContext.For("""{"milliseconds":10}"""), CancellationToken.None);

        JsonNode.Parse(result.OutputJson!)!["delayedMs"]!.GetValue<int>().Should().Be(10);
    }

    [Fact]
    public async Task Delay_honours_cancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => new DelayStepHandler().ExecuteAsync(HandlerContext.For("""{"milliseconds":60000}"""), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Delay_rejects_out_of_range_values()
    {
        var act = () => new DelayStepHandler().ExecuteAsync(HandlerContext.For("""{"milliseconds":-1}"""), CancellationToken.None);

        (await act.Should().ThrowAsync<StepFailedException>()).Which.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Transform_maps_fields_into_a_new_document()
    {
        var config = """
            {
              "source": { "order": { "id": "o-1", "lines": [ { "sku": "A" } ] }, "customer": "c-9" },
              "mappings": { "orderId": "order.id", "shipping.firstSku": "order.lines.0.sku", "customer": "customer", "missing": "nope" }
            }
            """;

        var result = await new TransformStepHandler().ExecuteAsync(HandlerContext.For(config), CancellationToken.None);

        result.OutputJson.Should().Be("""{"orderId":"o-1","shipping":{"firstSku":"A"},"customer":"c-9","missing":null}""");
    }

    [Fact]
    public async Task Transform_without_mappings_echoes_the_source_and_strict_mode_rejects_missing_paths()
    {
        var echo = await new TransformStepHandler().ExecuteAsync(HandlerContext.For("""{"source":{"a":1}}"""), CancellationToken.None);
        echo.OutputJson.Should().Be("""{"a":1}""");

        var act = () => new TransformStepHandler().ExecuteAsync(
            HandlerContext.For("""{"source":{"a":1},"mappings":{"b":"x.y"},"strict":true}"""),
            CancellationToken.None);
        (await act.Should().ThrowAsync<StepFailedException>()).Which.Retryable.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task Fault_injection_fails_the_first_n_attempts(int attempt, bool shouldFail)
    {
        var act = () => new FaultInjectionStepHandler().ExecuteAsync(HandlerContext.For("""{"mode":"fail","attempts":2}""", attempt), CancellationToken.None);

        if (shouldFail)
        {
            (await act.Should().ThrowAsync<StepFailedException>()).Which.Retryable.Should().BeTrue();
        }
        else
        {
            (await act()).OutputJson.Should().Contain("\"faultInjected\":false");
        }
    }

    [Fact]
    public async Task Fault_injection_can_fail_always_permanently_or_hang_until_cancelled()
    {
        var handler = new FaultInjectionStepHandler();

        var always = () => handler.ExecuteAsync(HandlerContext.For("""{"retryable":false,"message":"card declined"}""", attempt: 25), CancellationToken.None);
        (await always.Should().ThrowAsync<StepFailedException>().WithMessage("card declined")).Which.Retryable.Should().BeFalse();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var hang = () => handler.ExecuteAsync(HandlerContext.For("""{"mode":"hang"}"""), cts.Token);
        await hang.Should().ThrowAsync<OperationCanceledException>();

        var none = await handler.ExecuteAsync(HandlerContext.For("""{"mode":"none"}"""), CancellationToken.None);
        none.OutputJson.Should().Contain("\"faultInjected\":false");
    }

    [Fact]
    public async Task Log_handler_echoes_the_message()
    {
        var handler = new LogStepHandler(NullLogger<LogStepHandler>.Instance);

        var result = await handler.ExecuteAsync(HandlerContext.For("""{"message":"hello","level":"warning"}"""), CancellationToken.None);

        result.OutputJson.Should().Be("""{"message":"hello","level":"Warning"}""");
    }

    [Fact]
    public void Resolver_finds_handlers_case_insensitively_and_rejects_duplicates()
    {
        var mock = Substitute.For<IStepHandler>();
        mock.Type.Returns("custom");
        var resolver = new StepHandlerResolver(new IStepHandler[] { new DelayStepHandler(), mock });

        resolver.Find("CUSTOM").Should().BeSameAs(mock);
        resolver.Find("missing").Should().BeNull();
        resolver.RegisteredTypes.Should().Equal("custom", "delay");

        var duplicate = Substitute.For<IStepHandler>();
        duplicate.Type.Returns("delay");
        var act = () => new StepHandlerResolver(new IStepHandler[] { new DelayStepHandler(), duplicate });
        act.Should().Throw<InvalidOperationException>().WithMessage("*delay*");
    }
}
