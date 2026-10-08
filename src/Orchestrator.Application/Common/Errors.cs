namespace Orchestrator.Application.Common;

/// <summary>A field-level validation problem, rendered as <c>{ "field": ..., "message": ... }</c> by the API.</summary>
public sealed record ValidationFailure(string Field, string Message);

/// <summary>Request failed validation (400).</summary>
public sealed class RequestValidationException : Exception
{
    public RequestValidationException(string message, IReadOnlyList<ValidationFailure> errors)
        : base(message)
    {
        Errors = errors;
    }

    public IReadOnlyList<ValidationFailure> Errors { get; }
}

/// <summary>Resource does not exist (404).</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message)
        : base(message)
    {
    }

    public static NotFoundException For(string resource, object id) => new($"{resource} '{id}' was not found.");
}

/// <summary>Request conflicts with current state (409), e.g. idempotency-key reuse with a different payload.</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }
}
