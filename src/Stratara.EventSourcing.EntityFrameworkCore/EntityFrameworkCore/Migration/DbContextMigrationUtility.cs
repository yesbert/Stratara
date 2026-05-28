using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Stratara.EventSourcing.EntityFrameworkCore.Migration;

/// <summary>
/// Helper for applying pending EF Core migrations across a set of DbContext option builders —
/// typically used by host startup to ensure the target databases exist and are up to date
/// before workers start.
/// </summary>
public static class DbContextMigrationUtility
{
    /// <summary>
    /// For each <see cref="DbContextOptionsBuilder{TContext}"/> in <paramref name="optionsBuilders"/>,
    /// creates a context, ensures its database exists, and applies any pending migrations.
    /// </summary>
    /// <typeparam name="TDbContext">The DbContext type being migrated.</typeparam>
    /// <param name="serviceProvider">The host service provider used to create a scope.</param>
    /// <param name="optionsBuilders">Pre-configured option builders, one per target database.</param>
    /// <returns>A task that completes when every database has been migrated.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <typeparamref name="TDbContext"/> cannot be instantiated via its <see cref="DbContextOptions{TContext}"/> constructor.</exception>
    public static async Task MigrateAllDatabasesAsync<TDbContext>(this IServiceProvider serviceProvider,
        IEnumerable<DbContextOptionsBuilder<TDbContext>> optionsBuilders) where TDbContext : DbContext
    {
        using var scope = serviceProvider.CreateScope();

        foreach (var options in optionsBuilders.Select(builder => builder.Options))
        {
            var dbContext = Create(options);
            var pending = await dbContext.Database.GetPendingMigrationsAsync();
            if (!pending.Any())
            {
                continue;
            }

            await EnsureDatabaseAsync(dbContext);
            await dbContext.Database.MigrateAsync();
        }
    }

    private static TDbContext Create<TDbContext>(DbContextOptions<TDbContext> options) where TDbContext : DbContext
    {
        var instance = Activator.CreateInstance(typeof(TDbContext), options);
        if (instance is null)
        {
            throw new InvalidOperationException($"Could not create an instance of {typeof(TDbContext).Name}.");
        }

        return (TDbContext)instance;
    }

    private static async Task EnsureDatabaseAsync(DbContext dbContext)
    {
        var dbCreator = dbContext.GetService<IRelationalDatabaseCreator>();
        var doesNotExists = !await dbCreator.ExistsAsync();
        if (doesNotExists)
        {
            await dbCreator.CreateAsync();
        }
    }
}
