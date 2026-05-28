namespace Stratara.Abstractions.Entities;

/// <summary>
/// Common shape for a tenant-scoped persistence entity — combines <see cref="IEntity"/>
/// (own <c>Id</c>) and <see cref="IMultiTenant"/> (Subject tenant id).
/// </summary>
public interface ITenantEntity : IEntity, IMultiTenant;
