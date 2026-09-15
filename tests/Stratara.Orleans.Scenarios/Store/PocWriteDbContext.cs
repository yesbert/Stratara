using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;

namespace Stratara.Orleans.IntegrationTests.Store;

/// <summary>
/// The proof of concept's write store: the shipped model, unchanged. The commit-order readers add
/// their columns in a derived context of their own so this one stays a faithful copy of what a
/// consumer migrates to today.
/// </summary>
public sealed class PocWriteDbContext(DbContextOptions<PocWriteDbContext> options)
    : WriteDbContext<PocWriteDbContext>(options);
