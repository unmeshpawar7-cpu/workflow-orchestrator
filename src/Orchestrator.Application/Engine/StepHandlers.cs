using System.Text.Json;
using Orchestrator.Application.Common;

namespace Orchestrator.Application.Engine;

/// <summary>
/// A step (or compensation) implementation, resolved from DI by its <see cref="Type"/> name.
/// Handlers must be idempotent: delivery is at-least-once (see docs/execution-semantics.md).
/// </summary>
public interface IStepHandler
{
    /// <summary>Name used in workflow definitions, e.g. <c>http</c>.</summary>
    string Type { get; }

    /// <summary>
    /// Executes one attempt. Must honour <paramref name="cancellationToken"/> (timeout, cancellation, shutdown).
    /// Throw <see cref="StepFailedException"/> to control retryability; any other exception is retryable.
    /// </summary>
    Task<StepResult> ExecuteAsync(StepExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>Everything a handler may know about the attempt it is executing.</summary>
public sealed class StepExecutionContext
{
    public required Guid RunId { get; init; }

    public required string StepKey { get; init; }

    /// <summary>1-based attempt number (forward attempts or compensation attempts).</summary>
    public required int Attempt { get; init; }

    public required bool IsCompensation { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Handler configuration with all templates already resolved.</summary>
    public string? ConfigJson { get; init; }

    public string InputJson { get; init; } = "{}";

    /// <summary>For compensations: the output the step produced when it succeeded.</summary>
    public string? StepOutputJson { get; init; }

    /// <summary>Stable key a handler can forward downstream to make side effects idempotent across attempts.</summary>
    public string IdempotencyKey => $"{RunId:N}:{StepKey}:{(IsCompensation ? "compensate" : "execute")}";

    /// <summary>Deserialises the configuration; invalid configuration is a non-retryable failure.</summary>
    public T GetConfig<T>()
        where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(ConfigJson))
        {
            return new T();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(ConfigJson, JsonText.Web) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new StepFailedException($"Invalid handler configuration: {ex.Message}", retryable: false, ex);
        }
    }
}

/// <summary>Result of a successful attempt. Output must be JSON (or null).</summary>
public sealed record StepResult(string? OutputJson)
{
    public static StepResult Empty { get; } = new((string?)null);

    public static StepResult FromObject(object? output) =>
        new(output is null ? null : JsonText.Serialize(output));
}

/// <summary>A handler failure with explicit retry semantics.</summary>
public class StepFailedException : Exception
{
    public StepFailedException(string message, bool retryable = true, Exception? innerException = null)
        : base(message, innerException)
    {
        Retryable = retryable;
    }

    public bool Retryable { get; }
}

/// <summary>Resolves handlers registered in DI by their type name.</summary>
public interface IStepHandlerResolver
{
    IReadOnlyCollection<string> RegisteredTypes { get; }

    IStepHandler? Find(string type);
}

public sealed class StepHandlerResolver : IStepHandlerResolver
{
    private readonly Dictionary<string, IStepHandler> _handlers;

    public StepHandlerResolver(IEnumerable<IStepHandler> handlers)
    {
        _handlers = new Dictionary<string, IStepHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.Type, handler))
            {
                throw new InvalidOperationException($"More than one step handler is registered for type '{handler.Type}'.");
            }
        }
    }

    public IReadOnlyCollection<string> RegisteredTypes => _handlers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public IStepHandler? Find(string type) =>
        !string.IsNullOrWhiteSpace(type) && _handlers.TryGetValue(type, out var handler) ? handler : null;
}
