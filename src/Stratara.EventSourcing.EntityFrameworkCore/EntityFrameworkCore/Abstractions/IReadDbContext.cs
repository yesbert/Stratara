namespace Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

/// <summary>
/// Marker for the read-side DbContext that projections write into and queries read from.
/// </summary>
public interface IReadDbContext : IDbContext;
