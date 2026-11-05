using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

/// <summary>
/// A test that registers its own write unit of work before the test event store keeps it, as it does against the
/// production write store.
/// </summary>
public class PreRegisteredServicesTests
{
    [Fact]
    public void A_write_unit_of_work_registered_before_the_test_event_store_is_kept()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var own = new OwnUnitOfWork();
        var services = new ServiceCollection();
        services.AddScoped<IWriteUnitOfWork>(_ => own);

        services.AddStrataraTestingEventStore<StrataraTestWriteDbContext>(connection, EventStoreTestHost.DefaultTenantId);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Same(own, scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>());
        Assert.Single(services, d => d.ServiceType == typeof(IWriteUnitOfWork));
    }

    private sealed class OwnUnitOfWork : IWriteUnitOfWork
    {
        public Task<ITransaction> StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IEventStreamRepository CreateEventStreamRepository(ITransaction transaction) => throw new NotSupportedException();

        public IEventChainRepository CreateEventChainRepository(ITransaction transaction) => throw new NotSupportedException();

        public ISnapshotRepository CreateSnapshotRepository(ITransaction transaction) => throw new NotSupportedException();

        public ICommandAuditRepository CreateCommandAuditRepository(ITransaction transaction) => throw new NotSupportedException();

        public IOutboxRepository CreateOutboxRepository(ITransaction transaction) => throw new NotSupportedException();
    }
}
