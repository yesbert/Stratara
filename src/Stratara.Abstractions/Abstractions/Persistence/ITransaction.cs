namespace Stratara.Abstractions.Persistence;

/// <summary>
/// A scoped database transaction. Dispose to roll back implicitly; call
/// <see cref="SaveChangesAsync"/> to commit.
/// </summary>
public interface ITransaction : IAsyncDisposable
{
    /// <summary>Commit pending changes asynchronously.</summary>
    /// <returns>Number of rows affected by the commit.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
