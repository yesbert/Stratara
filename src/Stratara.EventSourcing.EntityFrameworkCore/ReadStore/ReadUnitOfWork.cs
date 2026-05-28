using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.Persistence;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore;

/// <summary>
/// Read-side unit of work over an <see cref="IReadDbContext"/>. Used directly by query handlers
/// that do not need projection-specific repositories — projection writers should use
/// <see cref="ProjectionsUnitOfWork{TDbContext}"/> instead.
/// </summary>
/// <typeparam name="TDbContext">The concrete read-store DbContext type.</typeparam>
/// <param name="contextFactory">Factory used to create a new DbContext per transaction.</param>
public class ReadUnitOfWork<TDbContext>(IDbContextFactory<TDbContext> contextFactory)
    : UnitOfWork<TDbContext>(contextFactory), IReadUnitOfWork where TDbContext : DbContext, IReadDbContext;
