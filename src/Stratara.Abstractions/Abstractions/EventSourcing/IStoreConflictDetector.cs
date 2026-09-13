namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Recognises a database provider's refusal of a duplicate stream version, so that the event
/// source can surface it as a <see cref="ConcurrencyException"/> on that provider.
/// </summary>
/// <remarks>
/// <para>
/// A version collision is an insert the store's unique index refuses, and every provider reports
/// that refusal with an exception of its own. The framework registers a detector for each provider
/// it ships a store registration for — PostgreSQL through <c>AddNpgsqlWriteDbContextFactory</c>,
/// SQLite through the test-support store — and consults every registered detector when a save
/// fails. A host that brings another provider registers its own detector alongside; the framework's
/// keep working beside it.
/// </para>
/// <para>
/// A detector receives the exception the persistence layer threw and is expected to walk its inner
/// exceptions itself, because which layer wraps the provider's exception is a provider detail.
/// </para>
/// </remarks>
public interface IStoreConflictDetector
{
    /// <summary>
    /// Returns whether <paramref name="exception"/>, or any exception it wraps, is the provider's
    /// refusal of a row that violates a unique constraint.
    /// </summary>
    /// <param name="exception">The exception a save threw.</param>
    /// <returns><see langword="true"/> when the failure is a unique-constraint violation of this detector's provider; otherwise <see langword="false"/>.</returns>
    bool IsUniqueViolation(Exception exception);
}
