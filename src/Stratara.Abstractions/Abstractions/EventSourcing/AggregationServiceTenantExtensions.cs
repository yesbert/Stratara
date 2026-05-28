using Stratara.Abstractions.Domain;

namespace Stratara.Abstractions.EventSourcing;

/// <summary>
/// Tenant-aware helpers on top of <see cref="IAggregationService"/>.
/// </summary>
public static class AggregationServiceTenantExtensions
{
    /// <summary>
    /// Aggregate a tenant-scoped aggregate AND assert that the loaded aggregate's
    /// Subject tenant id matches <paramref name="tenantId"/>. Returns <c>null</c> on
    /// either "not found" or "loaded but wrong tenant" — letting the caller map both
    /// to <c>NotFound</c> without leaking foreign-tenant existence.
    /// </summary>
    /// <typeparam name="TAggregate">A tenant-owning aggregate type.</typeparam>
    /// <param name="service">The aggregation service.</param>
    /// <param name="streamId">The aggregate's stream id.</param>
    /// <param name="tenantId">The required Subject tenant id (typically <c>sessionContext.TenantId</c>).</param>
    /// <param name="cancellationToken">Propagated to the read.</param>
    /// <returns>The aggregate if loaded and tenant-matching; <c>null</c> otherwise.</returns>
    public static async Task<TAggregate?> AggregateOwnedByTenantAsync<TAggregate>(
        this IAggregationService service,
        Guid streamId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
        where TAggregate : class, ITenantAggregate, new()
    {
        var aggregate = await service.AggregateAsync<TAggregate>(streamId, cancellationToken: cancellationToken);
        return aggregate?.TenantId == tenantId ? aggregate : null;
    }
}
