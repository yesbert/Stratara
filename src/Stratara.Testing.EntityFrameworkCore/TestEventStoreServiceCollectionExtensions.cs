using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore;
using Stratara.Testing;
using Stratara.Testing.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wires the real Stratara event-sourcing write stack against a shared in-memory SQLite database,
/// using the in-memory doubles from <c>Stratara.Testing</c> for the cross-cutting dependencies.
/// </summary>
public static class TestEventStoreServiceCollectionExtensions
{
    /// <summary>
    /// Register the event-sourcing write stack (<c>IEventSource</c>, <c>IAggregationService</c>,
    /// snapshots, the EF Core write store) over <paramref name="sharedConnection"/>, plus an
    /// <see cref="InMemoryKeyStore"/>, a <see cref="TestSessionContextProvider"/>, and a
    /// <see cref="RecordingEventBundleOutboxDispatcher"/>. Register your aggregates with
    /// <c>AddAggregatesFromAssemblyContaining&lt;T&gt;()</c> so event payload types deserialize.
    /// </summary>
    /// <typeparam name="TWriteDbContext">The concrete write <see cref="DbContext"/> (e.g. <see cref="StrataraTestWriteDbContext"/>).</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="sharedConnection">
    /// An open SQLite connection kept alive for the test's lifetime. It must stay open so every
    /// DbContext minted by the unit of work shares the same in-memory database. The caller owns it.
    /// </param>
    /// <param name="defaultTenantId">The tenant the preset session context is scoped to.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the service collection carries a host environment, or the environment variables
    /// state one, naming anything other than <c>Development</c>. Where no environment is stated at
    /// all — an ordinary unit test — the call is allowed.
    /// </exception>
    /// <example>
    /// The connection is shared so the in-memory database outlives a single context. Register the
    /// aggregates too, or persisted event payloads will not resolve:
    /// <code>
    /// var connection = new SqliteConnection("DataSource=:memory:");
    /// connection.Open();
    /// services.AddStrataraTestingEventStore&lt;AppWriteDbContext&gt;(connection, Guid.NewGuid());
    /// services.AddAggregatesFromAssemblyContaining&lt;Account&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraTestingEventStore<TWriteDbContext>(
        this IServiceCollection services,
        SqliteConnection sharedConnection,
        Guid defaultTenantId)
        where TWriteDbContext : DbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sharedConnection);

        TestSupportEnvironmentGuard.EnsureDevelopmentOrUnstated(services, nameof(AddStrataraTestingEventStore));

        // Register the doubles first so the production TryAdd fallbacks (DummyKeyStore, etc.) never win.
        services.TryAddSingleton<IKeyStore>(_ => new InMemoryKeyStore());
        services.TryAddSingleton<ISessionContextProvider>(_ =>
            new TestSessionContextProvider(TestSessionContext.ForTenant(defaultTenantId)));
        services.TryAddSingleton<IEventBundleOutboxDispatcher, RecordingEventBundleOutboxDispatcher>();

        services.AddDbContextFactory<TWriteDbContext>(
            (_, options) => options.UseSqlite(sharedConnection),
            ServiceLifetime.Scoped);
        services.TryAddScoped<IWriteDbContext>(sp => sp.GetRequiredService<IDbContextFactory<TWriteDbContext>>().CreateDbContext());
        services.AddScoped<IWriteUnitOfWork>(sp => new WriteUnitOfWork<TWriteDbContext>(
            sp.GetRequiredService<IDbContextFactory<TWriteDbContext>>(),
            sp.GetRequiredService<ISessionContextProvider>(),
            sp.GetRequiredService<ISecureJsonSerializer>()));

        services.AddSecurity();
        services.AddMapping();
        services.AddEventSourcing();
        services.AddTrustedTypeResolver();

        return services;
    }
}
