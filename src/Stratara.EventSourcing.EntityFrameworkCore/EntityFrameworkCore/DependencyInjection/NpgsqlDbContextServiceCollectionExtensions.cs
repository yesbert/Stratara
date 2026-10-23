using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using Npgsql;
using Stratara.EventSourcing.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.Abstractions.EventSourcing;
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
/// <remarks>
/// A connection string that names no maximum pool size gets 15 rather than Npgsql's 100: one host registers several
/// contexts over one database, and several hosts share that database's <c>max_connections</c>. A connection string
/// that names one keeps it, whatever its value. Contexts that share a connection string share its pool, so on a host
/// that runs several roles every role draws from it. The size in effect is logged once per host and database, when the
/// host creates the first context for it.
/// </remarks>
public static partial class NpgsqlDbContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers an Npgsql-backed <see cref="IDbContextFactory{TContext}"/> for a write-store
    /// DbContext together with the write-side unit of work over it, and the default
    /// <see cref="IDbResolver"/> if none has been registered yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This binds the write store to the host: the <see cref="IWriteUnitOfWork"/> that the event
    /// source, the outbox dispatcher and the command worker depend on is registered here, scoped,
    /// as <c>WriteUnitOfWork&lt;TDbContext&gt;</c> over this context. A host that registers its own
    /// <see cref="IWriteUnitOfWork"/> — before or after this call — keeps it.
    /// </para>
    /// <para>
    /// The unit of work also takes the ambient <see cref="ISessionContextProvider"/> and the
    /// <see cref="ISecureJsonSerializer"/>, which come from <c>AddSessionContext()</c> and
    /// <c>AddSecurity()</c> — applied by every worker composite, and to be called explicitly by a
    /// host that composes its own role. They are resolved when the unit of work is first used, so
    /// the order of the <c>Add*</c> calls does not matter.
    /// </para>
    /// <para>
    /// The registration also contributes the <see cref="IStoreConflictDetector"/> that recognises
    /// PostgreSQL's unique-violation error, so a duplicate stream version surfaces as a
    /// <see cref="ConcurrencyException"/>. Detectors accumulate: a host that adds one for another
    /// provider does not displace this one, and applying this registration twice adds it once.
    /// </para>
    /// </remarks>
    /// <typeparam name="TDbContext">The concrete write-store DbContext type.</typeparam>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <example>
    /// Reads the connection string named <c>defaultdb</c>; with the session and security composites
    /// in place, nothing else is needed for the store:
    /// <code>
    /// services.AddNpgsqlWriteDbContextFactory&lt;AppWriteDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddNpgsqlWriteDbContextFactory<TDbContext>(this IServiceCollection services) where TDbContext : DbContext, IWriteDbContext
    {
        services.TryAddSingleton<PoolSizeReport>();
        services.AddDbContextFactory<TDbContext>((sp, options) => ConfigureDbOptions(options, sp), ServiceLifetime.Scoped);
        services.TryAddScoped<IWriteDbContext>(sp => sp.GetRequiredService<IDbContextFactory<TDbContext>>().CreateDbContext());
        services.TryAddScoped<IWriteUnitOfWork>(sp => new WriteUnitOfWork<TDbContext>(
            sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
            sp.GetRequiredService<ISessionContextProvider>(),
            sp.GetRequiredService<ISecureJsonSerializer>()));
        services.TryAddScoped<IDbResolver, DefaultDbResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStoreConflictDetector, PostgresConflictDetector>());
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
    /// own read-side unit of work, before or after this call, keeps it. The store that keeps the tenants
    /// projections have seen deleted is registered here too, as <c>AddStrataraForgottenTenants</c> would.
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
        services.TryAddSingleton<PoolSizeReport>();
        services.AddDbContextFactory<TDbContext>((sp, options) => ConfigureDbOptions(options, sp), ServiceLifetime.Scoped);
        services.TryAddScoped<IProjectionsUnitOfWork>(sp =>
            new ProjectionsUnitOfWork<TDbContext>(sp.GetRequiredService<IDbContextFactory<TDbContext>>()));
        services.TryAddScoped<IReadUnitOfWork>(sp => sp.GetRequiredService<IProjectionsUnitOfWork>());
        services.TryAddScoped<IDbResolver, DefaultDbResolver>();
        services.AddStrataraForgottenTenants<TDbContext>();
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
        services.TryAddSingleton<PoolSizeReport>();
        services.AddDbContextFactory<TDbContext>((sp, options) => ConfigureDbOptions(options, sp), ServiceLifetime.Scoped);
        services.TryAddScoped<TDbContext>(sp => sp.GetRequiredService<IDbContextFactory<TDbContext>>().CreateDbContext());
        services.TryAddScoped<IDbResolver, DefaultDbResolver>();
        return services;
    }

    /// <summary>The maximum pool size a connection string that names none is given.</summary>
    internal const int DefaultMaxPoolSize = 15;

    /// <summary>The log category of the pool-size line, under the framework's own prefix rather than the extension's namespace.</summary>
    internal const string PoolLogCategory = "Stratara.EventSourcing.EntityFrameworkCore.ConnectionPool";

    private static void ConfigureDbOptions(DbContextOptionsBuilder options, IServiceProvider sp)
    {
        var connectionString = EnsureMaxPoolSize(ResolveTenantConnectionString(sp), sp.GetService<PoolSizeReport>(), sp.GetService<ILoggerFactory>());
        options.UseSnakeCaseNamingConvention()
            .UseNpgsql(connectionString, o => o.UseVector())
            .AddInterceptors(CommitCompletionInterceptor.Instance)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.NoEntityTypeConfigurationsWarning));
    }

    /// <summary>
    /// Applies <see cref="DefaultMaxPoolSize"/> where <paramref name="connectionString"/> names no maximum pool size, and
    /// reports the size in effect through <paramref name="report"/> the first time the host meets that database.
    /// </summary>
    internal static string EnsureMaxPoolSize(string connectionString, PoolSizeReport? report, ILoggerFactory? loggers)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var named = NamesMaxPoolSize(connectionString);
        if (!named)
        {
            builder.MaxPoolSize = DefaultMaxPoolSize;
        }

        if (report is not null && loggers is not null)
        {
            report.Report(builder, named, loggers.CreateLogger(PoolLogCategory));
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Whether the connection string as the host gave it names a maximum pool size, under any keyword Npgsql takes for
    /// it: every key is applied to two builders preset to two different sizes, and a key that names the size moves at
    /// least one of them, whatever value it gives.
    /// </summary>
    internal static bool NamesMaxPoolSize(string connectionString)
    {
        var given = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var first = new NpgsqlConnectionStringBuilder { MaxPoolSize = 1 };
        var second = new NpgsqlConnectionStringBuilder { MaxPoolSize = 2 };
        foreach (string key in given.Keys)
        {
            try
            {
                first[key] = given[key];
                second[key] = given[key];
            }
            catch (ArgumentException)
            {
                // A key Npgsql does not take names nothing it would apply; the context reports it when it connects.
            }
        }

        return first.MaxPoolSize != 1 || second.MaxPoolSize != 2;
    }

    private static string ResolveTenantConnectionString(IServiceProvider sp)
    {
        var dbResolver = sp.GetRequiredService<IDbResolver>();
        return dbResolver.ResolveConnectionString();
    }

    [LoggerMessage(
        EventId = Stratara.Diagnostics.LogEvents.EventStore.PoolSizeInEffect,
        Level = LogLevel.Information,
        Message = "Connections to database {Database} on {Host} use a pool of at most {MaxPoolSize}, from {Origin}.")]
    internal static partial void LogPoolSizeInEffect(this ILogger logger, int maxPoolSize, string origin, string database, string host);
}
