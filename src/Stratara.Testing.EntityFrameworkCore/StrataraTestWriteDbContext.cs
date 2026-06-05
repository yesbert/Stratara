using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;

namespace Stratara.Testing.EntityFrameworkCore;

/// <summary>
/// A ready-made concrete write-store <see cref="DbContext"/> for tests, so consumers do not have to
/// declare their own <c>WriteDbContext&lt;T&gt;</c> subclass. It inherits the framework's
/// <see cref="WriteDbContext{TContext}"/> model (event stream, snapshot, command log, outbox) and
/// runs on any relational provider — the test host points it at SQLite in-memory.
/// </summary>
/// <param name="options">The EF Core options (supplied by the test host's SQLite factory).</param>
public sealed class StrataraTestWriteDbContext(DbContextOptions<StrataraTestWriteDbContext> options)
    : WriteDbContext<StrataraTestWriteDbContext>(options);
