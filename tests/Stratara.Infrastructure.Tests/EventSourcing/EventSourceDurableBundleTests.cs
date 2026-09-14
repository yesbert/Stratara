using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Contracts.Messages;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>event-sourcing-store</c> → <em>A batch is saved with durable bundles</em>, on the SQLite test
/// host: a dispatcher that stores with the commit receives the transaction the entries are staged
/// in, so what it writes there is durable in the same commit; and a store call that throws fails the
/// save before anything is committed.
/// </summary>
public class EventSourceDurableBundleTests
{
    private sealed class Counter
    {
        public int Value { get; set; }
    }

    private sealed record Incremented(int By);

    /// <summary>Stores a marker row through the transaction it is handed, or throws when told to.</summary>
    private sealed class StoringDispatcher(IWriteUnitOfWork unitOfWork, StoringDispatcherControl control) : IEventBundleOutboxDispatcher
    {
        public bool StoresBundlesWithCommit => true;

        public async Task StoreEventBundleAsync(EventBundle eventBundle, ITransaction transaction, CancellationToken cancellationToken = default)
        {
            if (control.ThrowOnStore)
            {
                throw new InvalidOperationException("the durable store refused");
            }

            await unitOfWork.CreateOutboxRepository(transaction).AddAsync(control.StoredId, eventBundle, cancellationToken);
            control.Stored.Add(eventBundle);
        }

        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default)
        {
            control.Enqueued.Add(eventBundle);
            return Task.CompletedTask;
        }

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StoringDispatcherControl
    {
        public Guid StoredId { get; } = Guid.CreateVersion7();
        public bool ThrowOnStore { get; set; }
        public List<EventBundle> Stored { get; } = [];
        public List<EventBundle> Enqueued { get; } = [];
    }

    /// <summary>
    /// The write stack over SQLite with the storing dispatcher in place of the recording one; the
    /// test-support host insists on the recording dispatcher, so the provider is built here.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Host(SqliteConnection connection, ServiceProvider provider, StoringDispatcherControl control)
        {
            _connection = connection;
            Services = provider;
            Control = control;
        }

        public ServiceProvider Services { get; }

        public StoringDispatcherControl Control { get; }

        public static Host Create()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            connection.Open();
            var control = new StoringDispatcherControl();

            var services = new ServiceCollection();
            services.AddSingleton(control);
            services.AddScoped<IEventBundleOutboxDispatcher, StoringDispatcher>();
            services.AddStrataraTestingEventStore<StrataraTestWriteDbContext>(connection, EventStoreTestHost.DefaultTenantId);
            services.AddTrustedType<Counter>().AddTrustedType<Incremented>();
            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                using var context = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>().CreateDbContext();
                context.Database.EnsureCreated();
            }

            return new Host(connection, provider, control);
        }

        public async Task SaveAsync(Guid streamId)
        {
            await using var scope = Services.CreateAsyncScope();
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Counter>(streamId, new Incremented(1));
            await events.SaveChangesAsync();
        }

        public async Task<bool> StreamExistsAsync(Guid streamId)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IEventSource>().ExistsAsync(streamId);
        }

        public async Task<int> StoredRowsAsync(Guid id)
        {
            await using var scope = Services.CreateAsyncScope();
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>().CreateDbContextAsync();
            return await context.Set<OutboxEntry>().CountAsync(e => e.Id == id);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SaveChangesAsync_WithADispatcherThatStoresWithTheCommit_TheStoredRowIsDurableWithTheEvents()
    {
        await using var host = Host.Create();
        var streamId = Guid.CreateVersion7();

        await host.SaveAsync(streamId);

        var stored = Assert.Single(host.Control.Stored);
        Assert.Same(stored, Assert.Single(host.Control.Enqueued));
        Assert.Equal(1, await host.StoredRowsAsync(host.Control.StoredId));
        Assert.True(await host.StreamExistsAsync(streamId));
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTheStoreCallThrows_NothingIsCommittedAndNothingIsPublished()
    {
        await using var host = Host.Create();
        host.Control.ThrowOnStore = true;
        var streamId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveAsync(streamId));

        Assert.Empty(host.Control.Enqueued);
        Assert.Equal(0, await host.StoredRowsAsync(host.Control.StoredId));
        Assert.False(await host.StreamExistsAsync(streamId));
    }
}
