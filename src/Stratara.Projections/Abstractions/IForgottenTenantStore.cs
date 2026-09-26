namespace Stratara.Projections.Abstractions;

/// <summary>
/// Keeps, for each projection that declares <see cref="IForgetsDeletedTenants"/>, the tenants it has
/// seen deleted. The record is derived from the facts the projection applied and is emptied with its
/// read model.
/// </summary>
/// <remarks>
/// A projection is identified by the name the projection handler gives it, which is also the name its
/// checkpoints and a rebuild use. The read store's implementation is registered by
/// <c>AddNpgsqlReadDbContextFactory</c>, or by <c>AddStrataraForgottenTenants</c> for a read context
/// registered another way.
/// </remarks>
public interface IForgottenTenantStore
{
    /// <summary>Records that <paramref name="projection"/> has seen <paramref name="tenantIds"/> deleted.</summary>
    /// <param name="projection">The projection's name.</param>
    /// <param name="tenantIds">The deleted tenants. A tenant already recorded for the projection is left as it is.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes when every tenant is recorded.</returns>
    Task ForgetAsync(string projection, IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="projection"/> has seen <paramref name="tenantId"/> deleted.</summary>
    /// <param name="projection">The projection's name.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns><see langword="true"/> when the tenant is recorded for the projection.</returns>
    Task<bool> HasForgottenAsync(string projection, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Empties the record of one projection, as its read model is emptied for a rebuild.</summary>
    /// <param name="projection">The projection's name.</param>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes when the projection's record is empty.</returns>
    Task ClearAsync(string projection, CancellationToken cancellationToken = default);

    /// <summary>Empties the record of every projection, as the read models are emptied for a replay.</summary>
    /// <param name="cancellationToken">Propagated to the store.</param>
    /// <returns>A task that completes when every record is empty.</returns>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
