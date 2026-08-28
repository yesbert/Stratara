using Microsoft.EntityFrameworkCore;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Supplies the database context a directory store uses for one operation. The registration decides
/// whether that context is the request's own — shared with everything else in the scope — or one
/// created for the operation and disposed with it.
/// </summary>
internal interface IDirectoryContextSource<TContext> where TContext : DbContext
{
    Task<DirectoryContextLease<TContext>> LeaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A context held for the duration of one directory operation. Disposing the lease disposes the
/// context only when the lease owns it; a borrowed context outlives the operation and belongs to
/// whoever registered it.
/// </summary>
internal sealed class DirectoryContextLease<TContext>(TContext context, bool owned) : IAsyncDisposable
    where TContext : DbContext
{
    public TContext Context { get; } = context;

    public ValueTask DisposeAsync() => owned ? Context.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>
/// Hands out the context registered for the current scope. Every store in the scope gets the same
/// instance, so operations cannot overlap and a store's commit also commits whatever else is tracked
/// on it.
/// </summary>
internal sealed class SharedDirectoryContextSource<TContext>(TContext context) : IDirectoryContextSource<TContext>
    where TContext : DbContext
{
    public Task<DirectoryContextLease<TContext>> LeaseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DirectoryContextLease<TContext>(context, owned: false));
}

/// <summary>
/// Creates a context for each operation and disposes it afterwards, so operations do not contend and
/// a store's commit reaches only its own rows.
/// </summary>
internal sealed class PerOperationDirectoryContextSource<TContext>(IDbContextFactory<TContext> factory)
    : IDirectoryContextSource<TContext>
    where TContext : DbContext
{
    public async Task<DirectoryContextLease<TContext>> LeaseAsync(CancellationToken cancellationToken = default) =>
        new(await factory.CreateDbContextAsync(cancellationToken), owned: true);
}
