using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.Testing.EntityFrameworkCore;

/// <summary>
/// A self-contained test host that runs the real Stratara event-sourcing write stack against a
/// shared in-memory SQLite database. Construct it once per test, append events through the genuine
/// <see cref="IEventSource"/>, and read aggregates back through the genuine
/// <see cref="IAggregationService"/> — exercising production code paths without Postgres or Docker.
/// </summary>
/// <remarks>
/// Register the aggregates under test via the <c>configure</c> callback
/// (<c>services.AddAggregatesFromAssemblyContaining&lt;T&gt;()</c>) so event payload types
/// deserialize. Dispose the host (it is <see cref="IAsyncDisposable"/>) to close the SQLite
/// connection and tear down the database.
/// </remarks>
/// <example>
/// <code>
/// await using var host = EventStoreTestHost.Create(s => s.AddAggregatesFromAssemblyContaining&lt;Account&gt;());
///
/// await host.ExecuteAsync(async events =>
/// {
///     await events.CreateAsync&lt;Account&gt;(id, new AccountOpened(id, tenantId, "Ada", 100m));
///     await events.SaveChangesAsync();
/// });
///
/// var account = await host.AggregateAsync&lt;Account&gt;(id);
/// Assert.Equal(100m, account!.Balance);
/// </code>
/// </example>
public sealed class EventStoreTestHost : IAsyncDisposable
{
    /// <summary>The tenant id the host's default session context is scoped to.</summary>
    public static readonly Guid DefaultTenantId = new("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    private EventStoreTestHost(SqliteConnection connection, ServiceProvider provider)
    {
        _connection = connection;
        _provider = provider;
        Session = (TestSessionContextProvider)provider.GetRequiredService<ISessionContextProvider>();
        Outbox = (RecordingEventBundleOutboxDispatcher)provider.GetRequiredService<IEventBundleOutboxDispatcher>();
    }

    /// <summary>The preset session provider — reassign its context to switch the acting tenant/user.</summary>
    public TestSessionContextProvider Session { get; }

    /// <summary>Records the event bundles emitted on each <c>SaveChangesAsync</c>, for assertions.</summary>
    public RecordingEventBundleOutboxDispatcher Outbox { get; }

    /// <summary>The root service provider, for resolving additional services in a scope.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>Create a host backed by the built-in <see cref="StrataraTestWriteDbContext"/>.</summary>
    /// <param name="configure">Optional extra registration (e.g. <c>AddAggregatesFromAssemblyContaining&lt;T&gt;()</c>).</param>
    /// <returns>A ready-to-use host with the schema created.</returns>
    public static EventStoreTestHost Create(Action<IServiceCollection>? configure = null) =>
        Create<StrataraTestWriteDbContext>(configure);

    /// <summary>Create a host backed by a caller-supplied write <see cref="DbContext"/> type.</summary>
    /// <typeparam name="TWriteDbContext">The concrete write context (must subclass <c>WriteDbContext&lt;T&gt;</c>).</typeparam>
    /// <param name="configure">Optional extra registration (e.g. <c>AddAggregatesFromAssemblyContaining&lt;T&gt;()</c>).</param>
    /// <returns>A ready-to-use host with the schema created.</returns>
    public static EventStoreTestHost Create<TWriteDbContext>(Action<IServiceCollection>? configure = null)
        where TWriteDbContext : DbContext, IWriteDbContext
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddStrataraTestingEventStore<TWriteDbContext>(connection, DefaultTenantId);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            using var context = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<TWriteDbContext>>()
                .CreateDbContext();
            context.Database.EnsureCreated();
        }

        return new EventStoreTestHost(connection, provider);
    }

    /// <summary>Run <paramref name="work"/> against the real <see cref="IEventSource"/> in a fresh scope.</summary>
    /// <param name="work">The append/create + <c>SaveChangesAsync</c> work to perform.</param>
    public async Task ExecuteAsync(Func<IEventSource, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = _provider.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<IEventSource>());
    }

    /// <summary>Rehydrate an aggregate through the real <see cref="IAggregationService"/> in a fresh scope.</summary>
    /// <typeparam name="TAggregate">The aggregate type — must have a public parameterless constructor.</typeparam>
    /// <param name="streamId">The aggregate's stream id.</param>
    /// <param name="cancellationToken">Propagated to the read.</param>
    /// <returns>The reconstructed aggregate, or <see langword="null"/> if the stream does not exist.</returns>
    public async Task<TAggregate?> AggregateAsync<TAggregate>(Guid streamId, CancellationToken cancellationToken = default)
        where TAggregate : notnull, new()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAggregationService>()
            .AggregateAsync<TAggregate>(streamId, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
