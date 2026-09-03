using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Stratara.EventSourcing.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Projections;

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
    /// DbContext together with the write-side unit of work over it, and the default
    /// <see cref="IDbResolver"/> if none has been registered yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is all the write store needs from the host: the <see cref="IWriteUnitOfWork"/> that the
    /// event source, the outbox dispatcher and the command worker depend on is registered here,
    /// scoped, as <c>WriteUnitOfWork&lt;TDbContext&gt;</c>. A host that registers its own
    /// <see cref="IWriteUnitOfWork"/> — before or after this call — keeps it.
    /// </para>
    /// <para>
    /// The unit of work also takes the ambient <see cref="ISessionContextProvider"/> and the
    /// <see cref="ISecureJsonSerializer"/>; both come from the session and security composites,
    /// which every worker composite applies. They are resolved when the unit of work is first used,
    /// so the order of the <c>Add*</c> calls does not matter.
    /// </para>
    /// </remarks>
    /// <typeparam name="TDbContext">The concrete write-store DbContext type.</typeparam>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <example>
    /// Reads the connection string named <c>defaultdb</c>; nothing else is needed for the store:
    /// <code>
    /// services.AddNpgsqlWriteDbContextFactory&lt;AppWriteDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddNpgsqlWriteDbContextFactory<TDbContext>(this IServiceCollection services) where TDbContext : DbContext, IWriteDbContext
    {
        services.AddDbContextFactory<TDbContext>((sp, options) => ConfigureDbOptions(options, sp), ServiceLifetime.Scoped);
        services.TryAddScoped<IWriteDbContext>(sp => sp.GetRequiredService<IDbContextFactory<TDbContext>>().CreateDbContext());
        services.TryAddScoped<IWriteUnitOfWork>(sp => new WriteUnitOfWork<TDbContext>(
            sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
            sp.GetRequiredService<ISessionContextProvider>(),
            sp.GetRequiredService<ISecureJsonSerializer>()));
        services.TryAddScoped<IDbResolver, DefaultDbResolver>();
        return services;
    }

    /// <summary>
    /// Registers an Npgsql-backed <see cref="IDbContextFactory{TContext}"/> for a read-store
    /// DbContext together with the read-side unit of work over it, and the default
    /// <see cref="IDbResolver"/> if none has been registered yet.
    /// </summary>
    /// <remarks>
    /// The <see cref="IProjectionsUnitOfWork"/> that projections query and write through — and the
    /// <see cref="IReadUnitOfWork"/> it derives from, resolving to the same scoped instance — is
    /// registered here as <c>ProjectionsUnitOfWork&lt;TDbContext&gt;</c>. A host that registers its
    /// own read-side unit of work, before or after this call, keeps it.
    /// </remarks>
    /// <typeparam name="TDbContext">The concrete read-store DbContext type.</typeparam>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddNpgsqlReadDbContextFactory&lt;AppReadDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddNpgsqlReadDbContextFactory<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext, IReadDbContext
    {
        services.AddDbContextFactory<TDbContext>((sp, options) => ConfigureDbOptions(options, sp), ServiceLifetime.Scoped);
        services.TryAddScoped<IProjectionsUnitOfWork>(sp =>
            new ProjectionsUnitOfWork<TDbContext>(sp.GetRequiredService<IDbContextFactory<TDbContext>>()));
        services.TryAddScoped<IReadUnitOfWork>(sp => sp.GetRequiredService<IProjectionsUnitOfWork>());
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
    /// <example>
    /// Also resolves the context itself as scoped, so ASP.NET Core Identity can inject it directly:
    /// <code>
    /// services.AddNpgsqlIdentityDbContextFactory&lt;AppIdentityDbContext&gt;();
    /// </code>
    /// </example>
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
