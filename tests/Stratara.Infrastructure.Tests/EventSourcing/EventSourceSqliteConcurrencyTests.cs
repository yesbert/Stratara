using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.EventSourcing;
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
}
