using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.EventSourcing.EntityFrameworkCore.Migration;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore.Tests;

public class DbContextMigrationUtilityTests
{
    [Fact]
    public async Task Every_context_created_to_migrate_a_database_is_disposed()
    {
        await using var first = new SqliteConnection("Filename=:memory:");
        await using var second = new SqliteConnection("Filename=:memory:");
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var builders = new[] { first, second }
            .Select(connection => new DbContextOptionsBuilder<DisposalCountingContext>().UseSqlite(connection))
            .ToList();
        var before = DisposalCountingContext.Disposals;

        await provider.MigrateAllDatabasesAsync(builders);

        Assert.Equal(before + 2, DisposalCountingContext.Disposals);
    }

    /// <summary>A context with no migrations, counting how often it is disposed.</summary>
    private sealed class DisposalCountingContext(DbContextOptions<DisposalCountingContext> options) : DbContext(options)
    {
        private static int _disposals;

        public static int Disposals => Volatile.Read(ref _disposals);

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            return base.DisposeAsync();
        }

        public override void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            base.Dispose();
        }
    }
}
