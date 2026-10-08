using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Common;
using Orchestrator.Application.Engine;

namespace Orchestrator.Infrastructure.Handlers;

/// <summary>Configuration section <c>Handlers:Http</c>.</summary>
public sealed class HttpHandlerOptions
{
    public const string SectionName = "Handlers:Http";

    /// <summary>When non-empty, only these hosts may be called (SSRF guard). Empty = any host.</summary>
    public List<string> AllowedHosts { get; set; } = new();

    /// <summary>Response bodies longer than this (characters) are truncated in the step output.</summary>
    public int MaxResponseChars { get; set; } = 64 * 1024;
}

public sealed class HttpStepConfig
{
    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public Dictionary<string, string>? Headers { get; set; }

    public JsonElement? Body { get; set; }

    /// <summary>Status codes treated as success; defaults to any 2xx.</summary>
    public List<int>? SuccessStatusCodes { get; set; }
}

/// <summary>
/// Calls an HTTP endpoint (typed <see cref="HttpClient"/>). 5xx/408/429 and transport errors are retryable;
/// other non-success codes are permanent. The step's idempotency key is forwarded as <c>Idempotency-Key</c>.
/// No Polly retries here on purpose: the engine owns retries, timeouts and backoff.
/// </summary>
public sealed class HttpStepHandler : IStepHandler
{
    public const string TypeName = "http";

    private readonly HttpClient _client;
    private readonly HttpHandlerOptions _options;

    public HttpStepHandler(HttpClient client, IOptions<HttpHandlerOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    public string Type => TypeName;

    public async Task<StepResult> ExecuteAsync(StepExecutionContext context, CancellationToken cancellationToken)
    {
        var config = context.GetConfig<HttpStepConfig>();
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new StepFailedException("http: 'url' must be an absolute http(s) URL.", retryable: false);
        }

        if (_options.AllowedHosts.Count > 0 && !_options.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new StepFailedException($"http: host '{uri.Host}' is not listed in Handlers:Http:AllowedHosts.", retryable: false);
        }

        var method = new HttpMethod(string.IsNullOrWhiteSpace(config.Method) ? "GET" : config.Method.Trim().ToUpperInvariant());
        using var request = new HttpRequestMessage(method, uri);
        if (config.Body is JsonElement body && body.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        }

        foreach (var header in config.Headers ?? new Dictionary<string, string>())
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        request.Headers.TryAddWithoutValidation("Idempotency-Key", context.IdempotencyKey);
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", context.CorrelationId);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = (int)response.StatusCode;
        var success = config.SuccessStatusCodes is { Count: > 0 } codes
            ? codes.Contains(status)
            : response.IsSuccessStatusCode;

        if (!success)
        {
            var retryable = status >= 500 || status == 408 || status == 429;
            throw new StepFailedException($"http: {method} {uri.GetLeftPart(UriPartial.Path)} returned {status}.", retryable);
        }

        var truncated = text.Length > _options.MaxResponseChars;
        return StepResult.FromObject(new
        {
            statusCode = status,
            body = ParseBody(truncated ? text[.._options.MaxResponseChars] : text, truncated),
            truncated,
        });
    }

    private static JsonNode? ParseBody(string text, bool truncated)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!truncated)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // fall through: not JSON
            }
        }

        return JsonValue.Create(text);
    }
}

public sealed class DelayStepConfig
{
    public int Milliseconds { get; set; } = 1000;
}

/// <summary>Waits for <c>milliseconds</c> (max 1 hour), honouring cancellation and the step timeout.</summary>
public sealed class DelayStepHandler : IStepHandler
{
    public const string TypeName = "delay";
    private const int MaxMilliseconds = 3_600_000;

    public string Type => TypeName;

    public async Task<StepResult> ExecuteAsync(StepExecutionContext context, CancellationToken cancellationToken)
    {
        var config = context.GetConfig<DelayStepConfig>();
        if (config.Milliseconds < 0 || config.Milliseconds > MaxMilliseconds)
        {
            throw new StepFailedException($"delay: milliseconds must be between 0 and {MaxMilliseconds}.", retryable: false);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(config.Milliseconds), cancellationToken);
        return StepResult.FromObject(new { delayedMs = config.Milliseconds });
    }
}

public sealed class LogStepConfig
{
    public string? Message { get; set; }

    public string Level { get; set; } = "Information";
}

/// <summary>Writes a structured log entry (message is usually templated) and echoes it as output.</summary>
public sealed class LogStepHandler : IStepHandler
{
    public const string TypeName = "log";
    private const int MaxMessageLength = 2000;
    private readonly ILogger<LogStepHandler> _logger;

    public LogStepHandler(ILogger<LogStepHandler> logger)
    {
        _logger = logger;
    }

    public string Type => TypeName;

    public Task<StepResult> ExecuteAsync(StepExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = context.GetConfig<LogStepConfig>();
        var message = config.Message ?? string.Empty;
        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength];
        }

        var level = Enum.TryParse<LogLevel>(config.Level, ignoreCase: true, out var parsed) ? parsed : LogLevel.Information;
        _logger.Log(
            level,
            "Workflow log step {StepKey} (run {RunId}, compensation {IsCompensation}): {WorkflowMessage}",
            context.StepKey,
            context.RunId,
            context.IsCompensation,
            message);
        return Task.FromResult(StepResult.FromObject(new { message, level = level.ToString() }));
    }
}

public sealed class TransformStepConfig
{
    /// <summary>Source document (typically built from templates).</summary>
    public JsonElement? Source { get; set; }

    /// <summary>target dotted path -> source dotted path. Empty = output the source unchanged.</summary>
    public Dictionary<string, string>? Mappings { get; set; }

    /// <summary>When true, a missing source path fails the step instead of producing null.</summary>
    public bool Strict { get; set; }
}

/// <summary>Builds a new JSON object by mapping fields of <c>source</c> (pure, no I/O).</summary>
public sealed class TransformStepHandler : IStepHandler
{
    public const string TypeName = "transform";

    public string Type => TypeName;

    public Task<StepResult> ExecuteAsync(StepExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = context.GetConfig<TransformStepConfig>();
        var source = JsonText.FromElement(config.Source) is { } sourceJson ? JsonNode.Parse(sourceJson) : null;

        if (config.Mappings is null || config.Mappings.Count == 0)
        {
            return Task.FromResult(new StepResult(source?.ToJsonString() ?? "{}"));
        }

        var output = new JsonObject();
        foreach (var mapping in config.Mappings)
        {
            var targetPath = JsonPath.Split(mapping.Key);
            if (targetPath.Count == 0)
            {
                throw new StepFailedException("transform: mapping target paths must not be empty.", retryable: false);
            }

            if (JsonPath.TryGet(source, JsonPath.Split(mapping.Value), out var value))
            {
                JsonPath.Set(output, targetPath, value?.DeepClone());
            }
            else if (config.Strict)
            {
                throw new StepFailedException($"transform: source path '{mapping.Value}' was not found.", retryable: false);
            }
            else
            {
                JsonPath.Set(output, targetPath, null);
            }
        }

        return Task.FromResult(new StepResult(output.ToJsonString()));
    }
}

public sealed class FaultInjectionConfig
{
    /// <summary><c>fail</c> (default), <c>hang</c> (sleep until cancelled, i.e. past the timeout) or <c>none</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Inject the fault on attempts 1..N only; null = on every attempt.</summary>
    public int? Attempts { get; set; }

    /// <summary>Whether injected failures are retryable.</summary>
    public bool Retryable { get; set; } = true;

    public string? Message { get; set; }
}

/// <summary>
/// TESTING / CHAOS HANDLER for demos and tests - not for production workflows. Fails (or hangs past the step
/// timeout) on the first N attempts, or always, so retries, timeouts, compensation and dead-lettering can be
/// exercised deterministically. Decisions are based only on the attempt number, so they behave identically on
/// every worker instance.
/// </summary>
public sealed class FaultInjectionStepHandler : IStepHandler
{
    public const string TypeName = "fault-injection";

    public string Type => TypeName;

    public async Task<StepResult> ExecuteAsync(StepExecutionContext context, CancellationToken cancellationToken)
    {
        var config = context.GetConfig<FaultInjectionConfig>();
        var mode = string.IsNullOrWhiteSpace(config.Mode) ? "fail" : config.Mode.Trim().ToLowerInvariant();
        var injectNow = config.Attempts is null || context.Attempt <= config.Attempts.Value;

        if (mode is "none" or "succeed" || !injectNow)
        {
            return StepResult.FromObject(new { attempt = context.Attempt, faultInjected = false });
        }

        switch (mode)
        {
            case "fail":
                throw new StepFailedException(
                    config.Message ?? $"Injected failure on attempt {context.Attempt}.",
                    config.Retryable);
            case "hang":
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable: an infinite delay only ends by cancellation.");
            default:
                throw new StepFailedException($"fault-injection: unknown mode '{mode}' (use fail, hang or none).", retryable: false);
        }
    }
}
