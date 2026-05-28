namespace Stratara.Abstractions.Persistence;

/// <summary>
/// Common base for read + write units-of-work. Provides explicit transaction control
/// — callers obtain an <see cref="ITransaction"/>, run a sequence of repository
/// operations, then commit via <see cref="ITransaction.SaveChangesAsync"/>.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Open a new transaction asynchronously.</summary>
    Task<ITransaction> StartAsync(CancellationToken cancellationToken = default);
}
