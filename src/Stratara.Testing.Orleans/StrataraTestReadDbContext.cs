using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;

namespace Stratara.Testing.Orleans;

/// <summary>
/// A ready-made read-store <see cref="DbContext"/> for <see cref="ExecutionModelTestHost"/>: the framework's read model,
/// which holds the store readers' checkpoints, on the host's in-memory SQLite database.
/// </summary>
/// <param name="options">The EF Core options (supplied by the test host).</param>
public sealed class StrataraTestReadDbContext(DbContextOptions<StrataraTestReadDbContext> options)
    : ReadDbContext<StrataraTestReadDbContext>(options);
