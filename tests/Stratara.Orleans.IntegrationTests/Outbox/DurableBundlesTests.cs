using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;

namespace Stratara.Orleans.IntegrationTests.Outbox;

/// <summary>
/// <c>outbox-and-messaging</c> → <em>A host has opted in to durable bundles and the bus accepts</em>
/// and <em>… and the bus refuses</em>, on the PostgreSQL store with the RabbitMQ transport: with
/// <c>Outbox:DurableBundles</c> the bundle is in the outbox table when the events commit, is removed
/// once the bus has accepted it, and stays for the drain when the bus refuses — here because the
/// topic has no bound queue, which mandatory routing reports as a refusal.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DurableBundlesTests(PostgreSqlFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task Save_WithDurableBundles_TheBusAccepts_TheStoredBundleIsRemoved()
    {
        using var host = await StartHostAsync("durable_accept");
        var ids = host.Services.GetRequiredService<IMessagingIdentifier>();
        await host.Services.GetRequiredService<IMessageBus>().EnsureSubscriptionAsync(ids.EventBundleTopic, $"worker-{Guid.NewGuid():N}");

        await SaveOneAsync(host);

        Assert.Equal(0, await StoredBundlesAsync(host));
    }

    [Fact]
    public async Task Save_WithDurableBundles_TheBusRefuses_TheBundleStaysForTheDrainAndTheSaveSucceeds()
    {
        using var host = await StartHostAsync("durable_refuse");
        var ids = host.Services.GetRequiredService<IMessagingIdentifier>();

        var streamId = await SaveOneAsync(host);

        Assert.Equal(1, await StoredBundlesAsync(host));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IEventSource>().ExistsAsync(streamId));
        }

        await host.Services.GetRequiredService<IMessageBus>().EnsureSubscriptionAsync(ids.EventBundleTopic, $"worker-{Guid.NewGuid():N}");
        await DrainAsync(host);

        Assert.Equal(0, await StoredBundlesAsync(host));
    }

    private async Task<IHost> StartHostAsync(string database)
    {
        var store = postgres.ConnectionStringFor(database);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
            ["Outbox:DurableBundles"] = "true",
            ["Messaging:Topics:0:Name"] = "EventBundle",
            ["Messaging:Topics:0:Value"] = $"event-bundle-{database}-{Guid.NewGuid():N}",
        });
        builder.AddMessaging();
        builder.Services
            .AddSessionContext()
            .AddSecurity()
            .AddMapping()
            .AddResiliencePipelines()
            .AddEventSourcing()
            .AddOutboxDispatcher()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>();

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        await host.StartAsync();
        return host;
    }

    private static async Task<Guid> SaveOneAsync(IHost host)
    {
        var streamId = Guid.NewGuid();
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
        await events.SaveChangesAsync();
        return streamId;
    }

    private static async Task<int> StoredBundlesAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
        return await context.Set<OutboxEntry>().CountAsync();
    }

    private static async Task DrainAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        IReadOnlyList<OutboxEntry> entries;
        await using (var transaction = await unitOfWork.StartAsync())
        {
            entries = await unitOfWork.CreateOutboxRepository(transaction).GetManyAsync<EventBundle>(10, CancellationToken.None);
        }

        await scope.ServiceProvider.GetRequiredService<IEventBundleOutboxDispatcher>().EnqueueOutboxEntriesAsync(entries);
    }
}
