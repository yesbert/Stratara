using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Stratara.Abstractions.Persistence;
using Stratara.EventSourcing.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers <see cref="StrataraIdentityStore"/>, the identity store several hosts can share, and applies the
/// migrations shipped with it.
/// </summary>
public static class StrataraIdentityStoreServiceCollectionExtensions
{
    private const string DuplicateDatabase = "42P04";

    /// <summary>
    /// Registers <see cref="StrataraIdentityStore"/> on PostgreSQL together with the tenant directory's stores over it —
    /// memberships, API keys and settings, each with a context of its own per operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection is the connection string <see cref="StrataraIdentityStore.ConnectionStringName"/>, and where it
    /// is absent the host's default connection. Hosts that keep their own databases meet in the identity store by
    /// giving it the same connection.
    /// </para>
    /// <para>
    /// Register the accounts on it with <c>AddAspNetIdentity&lt;IdentityUser, StrataraIdentityStore&gt;()</c> (or the
    /// variant with the sign-in manager), and apply the migrations at start with
    /// <see cref="MigrateStrataraIdentityStoreAsync"/>. A host that registers the store generates no migrations of its
    /// own for these tables.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddStrataraIdentityStore();
    /// builder.AddAspNetIdentityWithSignInManager&lt;IdentityUser, StrataraIdentityStore&gt;();
    /// // after Build():
    /// await app.Services.MigrateStrataraIdentityStoreAsync();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraIdentityStore(this IServiceCollection services)
    {
        services.TryAddSingleton<PoolSizeReport>();
        services.AddDbContextFactory<StrataraIdentityStore>(
            (provider, options) => StrataraIdentityStoreOptionsBuilder.Configure(options, ResolveConnectionString(provider)),
            ServiceLifetime.Scoped);
        services.TryAddScoped(provider =>
            provider.GetRequiredService<IDbContextFactory<StrataraIdentityStore>>().CreateDbContext());
        services.TryAddScoped<IDbResolver, DefaultDbResolver>();
        services.AddTenantMembershipStoreFromContextFactory<StrataraIdentityStore>();
        services.AddApiKeyStoreFromContextFactory<StrataraIdentityStore>();
        services.AddSettingStoreFromContextFactory<StrataraIdentityStore>();
        return services;
    }

    /// <summary>
    /// Creates the identity store's database where it does not exist yet and applies the migrations shipped with
    /// <see cref="StrataraIdentityStore"/> that it has not applied.
    /// </summary>
    /// <remarks>
    /// Every host that shares the store applies the same migrations, so one a host applied is recognised as applied by
    /// every other. Hosts that migrate at the same time take turns: the migration lock of EF Core makes a second host
    /// wait for the first, and a database another host created a moment earlier counts as created.
    /// </remarks>
    /// <param name="services">The host's root service provider.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <returns>A task that completes once the store is migrated.</returns>
    public static async Task MigrateStrataraIdentityStoreAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await using var store = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<StrataraIdentityStore>>()
            .CreateDbContextAsync(cancellationToken);

        await EnsureDatabaseAsync(store.GetService<IRelationalDatabaseCreator>(), cancellationToken);
        await store.Database.MigrateAsync(cancellationToken);
    }

    private static async Task EnsureDatabaseAsync(IRelationalDatabaseCreator creator, CancellationToken cancellationToken)
    {
        if (await creator.ExistsAsync(cancellationToken))
        {
            return;
        }

        try
        {
            await creator.CreateAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == DuplicateDatabase)
        {
            return;
        }
    }

    private static string ResolveConnectionString(IServiceProvider provider)
    {
        var connectionString = provider.GetService<IConfiguration>()?.GetConnectionString(StrataraIdentityStore.ConnectionStringName)
                               ?? provider.GetRequiredService<IDbResolver>().ResolveConnectionString();
        return NpgsqlDbContextServiceCollectionExtensions.EnsureMaxPoolSize(
            connectionString,
            provider.GetService<PoolSizeReport>(),
            provider.GetService<ILoggerFactory>());
    }
}
