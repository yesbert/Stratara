namespace Stratara.Abstractions.Persistence;

/// <summary>
/// Thrown by an <see cref="ITransaction"/> implementation when an optimistic-concurrency
/// conflict is detected during commit (i.e. the row was modified or deleted by another
/// concurrent caller between the entity load and the save).
/// </summary>
/// <remarks>
/// Provider-agnostic wrapper that abstracts EF Core's <c>DbUpdateConcurrencyException</c>
/// (and equivalent provider exceptions) so framework-level code in <c>Stratara.Projections</c>
/// and consumers can react to concurrency conflicts without taking an EF Core dependency.
/// <para>
/// For projection delete handlers, callers may swallow this exception silently — a missing
/// row is the desired end-state of a delete. For projection update handlers, the conventional
/// response is to retry after a fresh read.
/// </para>
/// </remarks>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>Initializes a new instance with a default message.</summary>
    public ConcurrencyConflictException()
        : base("An optimistic-concurrency conflict was detected during commit.")
    {
    }

    /// <summary>Initializes a new instance with the supplied message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public ConcurrencyConflictException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with the supplied message and inner exception.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The provider-specific exception that caused this one.</param>
    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
