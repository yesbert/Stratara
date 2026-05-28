using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Stratara.EventSourcing.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Abstractions.Persistence;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-collection extensions that register Npgsql-backed
/// <see cref="IDbContextFactory{TContext}"/> instances for the Stratara write, read, and
/// identity stores with snake_case naming, pgvector support, and a reduced connection-pool
/// cap suitable for multi-tenant hosts.
/// </summary>
public static class NpgsqlDbContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers an Npgsql-backed <see cref="IDbContextFactory{TContext}"/> for a write-store
    /// DbContext along with the default <see cref="IDbResolver"/> if none has been registered yet.
    /// </summary>
    /// <typeparam name="TDbContext">The concrete write-store DbContext type.</typeparam>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddNpsqlWriteDbContextFactory<TDbContext>(this IServiceCollection services) where TDbContext : DbContext, IWriteDbContext
    {
        services.AddDbContextFactory<TDbContext>((sp, options) => ConfigureDbOptions(options, sp), ServiceLifetime.Scoped);
        services.TryAddScoped<IDbResolver, DefaultDbResolver>();
        return services;
    }

    /// <summary>
    /// Registers an Npgsql-backed <see cref="IDbContextFactory{TContext}"/> for a read-store
    /// DbContext along with the default <see cref="IDbResolver"/> if none has been registered yet.
    /// </summary>
    /// <typeparam name="TDbContext">The concrete read-store DbContext type.</typeparam>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddNpgsqlReadDbContextFactory<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext, IReadDbContext
    {
        services.AddDbContextFactory<TDbContext>((sp, options) => ConfigureDbOptions(options, sp), ServiceLifetime.Scoped);
        services.TryAddScoped<IDbResolver, DefaultDbResolver>();
        return services;
    }

    /// <summary>
    /// Registers an Npgsql-backed <see cref="IDbContextFactory{TContext}"/> for an identity-store
    /// DbContext together with a scoped resolution of the context itself (so ASP.NET Identity can
    /// inject it directly) and the default <see cref="IDbResolver"/>.
    /// </summary>
    /// <typeparam name="TDbContext">The concrete identity-store DbContext type.</typeparam>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddNpgsqlIdentityDbContextFactory<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext, IIdentityDbContext
    {
        services.AddDbContextFactory<TDbContext>((sp, options) => ConfigureDbOptions(options, sp), ServiceLifetime.Scoped);
        services.TryAddScoped<TDbContext>(sp => sp.GetRequiredService<IDbContextFactory<TDbContext>>().CreateDbContext());
        services.TryAddScoped<IDbResolver, DefaultDbResolver>();
        return services;
    }

    private const int NpgsqlDefaultMaxPoolSize = 100;
    private const int ReducedMaxPoolSize = 15;

    private static void ConfigureDbOptions(DbContextOptionsBuilder options, IServiceProvider sp)
    {
        var connectionString = EnsureMaxPoolSize(ResolveTenantConnectionString(sp));
        options.UseSnakeCaseNamingConvention()
            .UseNpgsql(connectionString, o => o.UseVector())
            .ConfigureWarnings(w => w.Ignore(CoreEventId.NoEntityTypeConfigurationsWarning));
    }

    private static string EnsureMaxPoolSize(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.MaxPoolSize == NpgsqlDefaultMaxPoolSize)
        {
            builder.MaxPoolSize = ReducedMaxPoolSize;
        }

        return builder.ConnectionString;
    }

    private static string ResolveTenantConnectionString(IServiceProvider sp)
    {
        var dbResolver = sp.GetRequiredService<IDbResolver>();
        return dbResolver.ResolveConnectionString();
    }
}
