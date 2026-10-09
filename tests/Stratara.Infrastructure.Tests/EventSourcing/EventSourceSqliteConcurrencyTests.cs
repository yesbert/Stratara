using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// Closes finding SF-003 of change <c>prove-an-orleans-execution-model</c>: a duplicate stream version
/// on the SQLite test host surfaces as <see cref="ConcurrencyException"/>, as it does on PostgreSQL,
/// because the store registration contributes the <see cref="IStoreConflictDetector"/> for its
/// provider. The second test pins the other half of the decision: with no detector registered the
/// framework assumes no provider, and the collision propagates as the persistence failure it was.
/// </summary>
public class EventSourceSqliteConcurrencyTests
{
    private sealed class Counter
    {
        public int Value { get; set; }
    }

    private sealed record Incremented(int By);

    [Fact]
    public async Task SaveChangesAsync_OnSqliteDuplicateVersion_SurfacesConcurrencyException()
    {
        await using var host = EventStoreTestHost.Create(services => services
            .AddTrustedType<Counter>()
            .AddTrustedType<Incremented>());
        var streamId = Guid.CreateVersion7();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Counter>(streamId, new Incremented(1));
            await events.SaveChangesAsync();
        });

        await using var first = host.Services.CreateAsyncScope();
        await using var second = host.Services.CreateAsyncScope();
        var firstWriter = first.ServiceProvider.GetRequiredService<IEventSource>();
        var secondWriter = second.ServiceProvider.GetRequiredService<IEventSource>();

        await firstWriter.AppendAsync<Counter>(streamId, new Incremented(2));
        await secondWriter.AppendAsync<Counter>(streamId, new Incremented(3));
        await firstWriter.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => secondWriter.SaveChangesAsync());

        Assert.Equal(streamId, ex.StreamId);
        Assert.IsType<DbUpdateException>(ex.InnerException);
    }

    [Fact]
    public async Task SaveChangesAsync_WithNoDetectorRegistered_SurfacesDbUpdateException()
    {
        await using var host = EventStoreTestHost.Create(services =>
        {
            services.RemoveAll<IStoreConflictDetector>();
            services.AddTrustedType<Counter>().AddTrustedType<Incremented>();
        });
        var streamId = Guid.CreateVersion7();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Counter>(streamId, new Incremented(1));
            await events.SaveChangesAsync();
        });

        await using var first = host.Services.CreateAsyncScope();
        await using var second = host.Services.CreateAsyncScope();
        var firstWriter = first.ServiceProvider.GetRequiredService<IEventSource>();
        var secondWriter = second.ServiceProvider.GetRequiredService<IEventSource>();

        await firstWriter.AppendAsync<Counter>(streamId, new Incremented(2));
        await secondWriter.AppendAsync<Counter>(streamId, new Incremented(3));
        await firstWriter.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => secondWriter.SaveChangesAsync());

        Assert.IsNotType<ConcurrencyException>(ex);
    }

    [Fact]
    public async Task AnAppendAfterARead_IsRecordedAfterAWriteThatLandedInBetween()
    {
        await using var host = CreateHost(appendAgainstAggregatedVersion: false);
        var streamId = await CreateStreamAsync(host);

        await using var handler = host.Services.CreateAsyncScope();
        await handler.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<Counter>(streamId);

        await AppendFromAnotherWriterAsync(host, streamId);

        var events = handler.ServiceProvider.GetRequiredService<IEventSource>();
        await events.AppendAsync<Counter>(streamId, new Incremented(3));
        await events.SaveChangesAsync();

        Assert.Equal(3, await CurrentVersionAsync(host, streamId));
    }

    [Fact]
    public async Task AppendAtVersion_WhenTheStreamMovedPastIt_TheSaveThrowsConcurrencyExceptionAndRecordsNothing()
    {
        await using var host = CreateHost(appendAgainstAggregatedVersion: false);
        var streamId = await CreateStreamAsync(host);

        await using var handler = host.Services.CreateAsyncScope();
        var events = handler.ServiceProvider.GetRequiredService<IEventSource>();
        var readVersion = await events.GetCurrentVersionAsync(streamId);

        await AppendFromAnotherWriterAsync(host, streamId);

        await events.AppendAtVersionAsync<Counter>(streamId, readVersion, new Incremented(3));
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => events.SaveChangesAsync());

        Assert.Equal(streamId, ex.StreamId);
        Assert.Equal(2, await CurrentVersionAsync(host, streamId));
    }

    [Fact]
    public async Task AppendAtVersion_WhenTheStreamDidNotMove_RecordsFromTheNextVersion()
    {
        await using var host = CreateHost(appendAgainstAggregatedVersion: false);
        var streamId = await CreateStreamAsync(host);

        await host.ExecuteAsync(async events =>
        {
            await events.AppendRangeAtVersionAsync<Counter>(streamId, 1, [new Incremented(2), new Incremented(3)]);
            await events.SaveChangesAsync();
        });

        Assert.Equal(3, await CurrentVersionAsync(host, streamId));
    }

    [Fact]
    public async Task AppendAgainstAggregatedVersion_WhenTheStreamMovedAfterTheRead_TheSaveThrowsConcurrencyException()
    {
        await using var host = CreateHost(appendAgainstAggregatedVersion: true);
        var streamId = await CreateStreamAsync(host);

        await using var handler = host.Services.CreateAsyncScope();
        await handler.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<Counter>(streamId);

        await AppendFromAnotherWriterAsync(host, streamId);

        var events = handler.ServiceProvider.GetRequiredService<IEventSource>();
        await events.AppendAsync<Counter>(streamId, new Incremented(3));
        await Assert.ThrowsAsync<ConcurrencyException>(() => events.SaveChangesAsync());

        Assert.Equal(2, await CurrentVersionAsync(host, streamId));
    }

    [Fact]
    public async Task AppendAgainstAggregatedVersion_WhenTheStreamWasCreatedAfterTheReadFoundNone_TheSaveThrowsConcurrencyException()
    {
        await using var host = CreateHost(appendAgainstAggregatedVersion: true);
        var streamId = Guid.CreateVersion7();

        await using var handler = host.Services.CreateAsyncScope();
        var found = await handler.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<Counter>(streamId);
        Assert.Null(found);

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Counter>(streamId, new Incremented(1));
            await events.SaveChangesAsync();
        });

        var events = handler.ServiceProvider.GetRequiredService<IEventSource>();
        await events.AppendAsync<Counter>(streamId, new Incremented(2));
        await Assert.ThrowsAsync<ConcurrencyException>(() => events.SaveChangesAsync());

        Assert.Equal(1, await CurrentVersionAsync(host, streamId));
    }

    [Fact]
    public async Task AppendAgainstAggregatedVersion_AReadBoundedToAPastVersion_SetsNoCondition()
    {
        await using var host = CreateHost(appendAgainstAggregatedVersion: true);
        var streamId = await CreateStreamAsync(host);
        await AppendFromAnotherWriterAsync(host, streamId);

        await using var handler = host.Services.CreateAsyncScope();
        await handler.ServiceProvider.GetRequiredService<IAggregationService>().AggregateAsync<Counter>(streamId, toVersion: 1);

        var events = handler.ServiceProvider.GetRequiredService<IEventSource>();
        await events.AppendAsync<Counter>(streamId, new Incremented(3));
        await events.SaveChangesAsync();

        Assert.Equal(3, await CurrentVersionAsync(host, streamId));
    }

    private static EventStoreTestHost CreateHost(bool appendAgainstAggregatedVersion) =>
        EventStoreTestHost.Create(services =>
        {
            services.AddTrustedType<Counter>().AddTrustedType<Incremented>();
            services.Configure<EventSourcingOptions>(options =>
                options.AppendAgainstAggregatedVersion = appendAgainstAggregatedVersion);
        });

    private static async Task<Guid> CreateStreamAsync(EventStoreTestHost host)
    {
        var streamId = Guid.CreateVersion7();
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Counter>(streamId, new Incremented(1));
            await events.SaveChangesAsync();
        });
        return streamId;
    }

    private static Task AppendFromAnotherWriterAsync(EventStoreTestHost host, Guid streamId) =>
        host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Counter>(streamId, new Incremented(2));
            await events.SaveChangesAsync();
        });

    private static async Task<long> CurrentVersionAsync(EventStoreTestHost host, Guid streamId)
    {
        long version = 0;
        await host.ExecuteAsync(async events => version = await events.GetCurrentVersionAsync(streamId));
        return version;
    }
}
