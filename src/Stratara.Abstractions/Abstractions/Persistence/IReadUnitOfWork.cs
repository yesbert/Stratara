namespace Stratara.Abstractions.Persistence;

/// <summary>
/// Read-side unit of work. Used for queries that need transactional consistency across
/// multiple repository reads (e.g. computing aggregations over a snapshot of the
/// read-store).
/// </summary>
public interface IReadUnitOfWork : IUnitOfWork;
