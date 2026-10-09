using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// An append made on the condition of the version the caller read is refused as a concurrency conflict on the
/// PostgreSQL store when another writer moved the stream in between — by the store's uniqueness of a stream's
/// versions, the same check that refuses two writers racing for one version.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ConditionalAppendTests(PostgreSqlFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task AppendAtVersion_WhenTheStreamMovedPastIt_TheSaveThrowsConcurrencyException()
    {
        using var app = await BuildAsync(appendAgainstAggregatedVersion: false);
        var tenantId = Guid.NewGuid();
        var counterId = Guid.NewGuid();
        await WriteAsync(app.Services, tenantId, events => events.CreateAsync<Counter>(counterId, new CounterCreated(counterId)));

        await using var handler = app.Services.CreateAsyncScope();
        handler.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = handler.ServiceProvider.GetRequiredService<IEventSource>();
        var readVersion = await events.GetCurrentVersionAsync(counterId);

        await WriteAsync(app.Services, tenantId, other => other.AppendAsync<Counter>(counterId, new CounterIncremented(counterId, 1)));

        await events.AppendAtVersionAsync<Counter>(counterId, readVersion, new CounterIncremented(counterId, 2));
        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() => events.SaveChangesAsync());

        Assert.Equal(counterId, conflict.StreamId);
        Assert.Equal(2, await VersionAsync(app.Services, tenantId, counterId));
    }

    [Fact]
    public async Task AppendAgainstAggregatedVersion_WhenTheStreamMovedAfterTheRead_TheSaveThrowsConcurrencyException()
    {
        using var app = await BuildAsync(appendAgainstAggregatedVersion: true);
        var tenantId = Guid.NewGuid();
        var counterId = Guid.NewGuid();
        await WriteAsync(app.Services, tenantId, events => events.CreateAsync<Counter>(counterId, new CounterCreated(counterId)));

        await using var handler = app.Services.CreateAsyncScope();
        handler.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        await handler.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<Counter>(counterId);

        await WriteAsync(app.Services, tenantId, other => other.AppendAsync<Counter>(counterId, new CounterIncremented(counterId, 1)));

        var events = handler.ServiceProvider.GetRequiredService<IEventSource>();
        await events.AppendAsync<Counter>(counterId, new CounterIncremented(counterId, 2));
        await Assert.ThrowsAsync<ConcurrencyException>(() => events.SaveChangesAsync());

        Assert.Equal(2, await VersionAsync(app.Services, tenantId, counterId));
    }

    [Fact]
    public async Task AConditionalSaveThatConflicts_IsRedeliveredByTheBus_AndTheHandlerRunsAgainOnTheStreamsEnd()
    {
        using var app = await BuildAsync(appendAgainstAggregatedVersion: true);
        var tenantId = Guid.NewGuid();
        var counterId = Guid.NewGuid();
        await WriteAsync(app.Services, tenantId, events => events.CreateAsync<Counter>(counterId, new CounterCreated(counterId)));

        var attempts = 0;
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var bus = app.Services.GetRequiredService<IMessageBus>();
        var topic = $"conditional-append-{Guid.NewGuid():N}";

        await bus.SubscribeAsync<IncrementRequested>(topic, $"worker-{Guid.NewGuid():N}", async request =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            await using var scope = app.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            await scope.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<Counter>(request.CounterId);
            if (attempt == 1)
            {
                await WriteAsync(app.Services, tenantId, other => other.AppendAsync<Counter>(request.CounterId, new CounterIncremented(request.CounterId, 1)));
            }

            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.AppendAsync<Counter>(request.CounterId, new CounterIncremented(request.CounterId, 10));
            await events.SaveChangesAsync();
            saved.TrySetResult();
        }, timeout.Token);

        await bus.PublishAsync(topic, new IncrementRequested(counterId), timeout.Token);
        await saved.Task.WaitAsync(timeout.Token);

        Assert.Equal(2, attempts);
        Assert.Equal(3, await VersionAsync(app.Services, tenantId, counterId));
    }

    /// <summary>The message the bus delivers to the handler that reads the counter and appends to it.</summary>
    public sealed record IncrementRequested(Guid CounterId);

    private static async Task WriteAsync(IServiceProvider services, Guid tenantId, Func<IEventSource, Task> append)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await append(events);
        await events.SaveChangesAsync();
    }

    private static async Task<long> VersionAsync(IServiceProvider services, Guid tenantId, Guid streamId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        return await scope.ServiceProvider.GetRequiredService<IEventSource>().GetCurrentVersionAsync(streamId);
    }

    private async Task<IHost> BuildAsync(bool appendAgainstAggregatedVersion)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_conditional_append_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
            ["EventSourcing:AppendAgainstAggregatedVersion"] = appendAgainstAggregatedVersion.ToString(),
        });
        builder.AddBackendServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<IncrementRequested>();

        var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        return app;
    }
}
