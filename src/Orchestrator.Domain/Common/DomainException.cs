namespace Orchestrator.Domain.Common;

/// <summary>
/// Raised when a domain invariant is violated (invalid argument for an aggregate, illegal operation for the
/// current state, ...). The API maps it to a 409/400 depending on the subtype.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Raised when a state machine is asked to perform a transition that is not in its transition table.
/// </summary>
public sealed class InvalidStateTransitionException : DomainException
{
    public InvalidStateTransitionException(string entity, string from, string to)
        : base($"Illegal {entity} state transition: {from} -> {to}.")
    {
        Entity = entity;
        From = from;
        To = to;
    }

    public string Entity { get; }

    public string From { get; }

    public string To { get; }
}
