namespace Stratara.Abstractions.Entities;

/// <summary>
/// Persistence-level entity carrying a stable <see cref="Id"/>. EF Core uses this to
/// configure value-generated primary keys uniformly across the framework.
/// </summary>
public interface IEntity
{
    /// <summary>The entity's primary-key identifier.</summary>
    Guid Id { get; set; }
}
