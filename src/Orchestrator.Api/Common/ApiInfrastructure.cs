using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orchestrator.Application.Common;
using Orchestrator.Domain.Common;
using Serilog.Context;

namespace Orchestrator.Api.Common;

/// <summary>Success envelope: <c>{ "success": true, "data": ..., "message": null }</c>.</summary>
public sealed record ApiResponse<T>(bool Success, T? Data, string? Message);

public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data, string? message = null) => new(true, data, message);
}

/// <summary>A single field error.</summary>
public sealed record ApiError(string? Field, string Message);

/// <summary>Error envelope: <c>{ "success": false, "message", "errors": [{ "field", "message" }], "traceId" }</c>.</summary>
public sealed record ApiErrorResponse(bool Success, string Message, IReadOnlyList<ApiError> Errors, string TraceId)
{
    public static ApiErrorResponse Create(string message, HttpContext context, IReadOnlyList<ApiError>? errors = null) =>
        new(false, message, errors ?? Array.Empty<ApiError>(), context.TraceIdentifier);
}

/// <summary>Maps exceptions to HTTP status codes and the error envelope (.NET 8 <see cref="IExceptionHandler"/>).</summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, message, errors) = Map(exception);
        if (status >= 500)
        {
            _logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogInformation("Request failed with {Status}: {Message}", status, message);
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(ApiErrorResponse.Create(message, httpContext, errors), cancellationToken);
        return true;
    }

    private static (int Status, string Message, IReadOnlyList<ApiError>? Errors) Map(Exception exception)
    {
        switch (exception)
        {
            case RequestValidationException validation:
                return (StatusCodes.Status400BadRequest, validation.Message, validation.Errors.Select(e => new ApiError(e.Field, e.Message)).ToList());
            case NotFoundException notFound:
                return (StatusCodes.Status404NotFound, notFound.Message, null);
            case ConflictException conflict:
                return (StatusCodes.Status409Conflict, conflict.Message, null);
            case InvalidStateTransitionException transition:
                return (StatusCodes.Status409Conflict, transition.Message, null);
            case DomainException domain:
                return (StatusCodes.Status422UnprocessableEntity, domain.Message, null);
            case DbUpdateConcurrencyException:
                return (StatusCodes.Status409Conflict, "The resource was modified concurrently; retry the request.", null);
            case BadHttpRequestException badRequest:
                return (badRequest.StatusCode, "Malformed request.", null);
            default:
                return (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", null);
        }
    }
}

/// <summary>
/// Accepts <c>X-Correlation-Id</c> (or generates one), uses it as the request trace id, echoes it in the response
/// and pushes it into the Serilog log context. Run creation stores it as the run's correlation id.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsValid(supplied) ? supplied : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    private static bool IsValid(string value) =>
        value.Length is > 0 and <= 100
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}

/// <summary>Configuration section <c>RateLimiting:RunCreation</c> (fixed window per API key).</summary>
public sealed class RunCreationRateLimitOptions
{
    public const string SectionName = "RateLimiting:RunCreation";
    public const string PolicyName = "run-creation";

    public int PermitLimit { get; set; } = 60;

    public int WindowSeconds { get; set; } = 60;
}

public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
            }),
        });
}
