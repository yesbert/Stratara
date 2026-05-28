namespace Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

/// <summary>
/// Implemented by DbContexts that carry an ambient tenant id used to install global query filters
/// on entities that implement <c>IMultiTenant</c>.
/// </summary>
public interface ITenantScopedDbContext
{
    /// <summary>Gets the data-owner tenant id the context is scoped to.</summary>
    Guid TenantId { get; }
}
