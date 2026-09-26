using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Testing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>event-sourcing-store</c> → a failure discards the whole staged batch, and a stated Subject applies to its one
/// event. Both matter to a handler that runs again in the same scope — a retrying pipeline does exactly that — because
/// whatever a failed attempt left behind would be written with the next one.
/// </summary>
public class EventSourceFailedBatchTests
{
    private sealed class FailedBatchProbe
    {
        public int Value { get; set; }
    }

    private sealed record FailedBatchProbeTouched(int By);

    /// <summary>
    /// A host with durable bundles records the bundle in the save's own transaction; this one fails to, once,
    /// so the save fails before anything is committed.
    /// </summary>
    private sealed class FailingOnceDurableHandover : IEventBundleOutboxDispatcher
    {
        private bool _failed;

        public bool StoresBundlesWithCommit => true;

        public Task StoreEventBundleAsync(EventBundle eventBundle, ITransaction transaction, CancellationToken cancellationToken = default)
        {
            if (_failed)
            {
                return Task.CompletedTask;
            }

            _failed = true;
            throw new InvalidOperationException("The store is briefly unavailable.");
        }

        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static EventStoreTestHost CreateHost(Action<IServiceCollection>? configure = null) =>
        EventStoreTestHost.Create(services =>
        {
            services.AddTrustedType<FailedBatchProbe>().AddTrustedType<FailedBatchProbeTouched>();
            configure?.Invoke(services);
        });

    private static async Task<Guid> CreateStreamAsync(EventStoreTestHost host)
    {
        var streamId = Guid.CreateVersion7();
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<FailedBatchProbe>(streamId, new FailedBatchProbeTouched(1));
            await events.SaveChangesAsync();
        });
        return streamId;
    }

    private static async Task<IReadOnlyList<EventStreamEntry>> ReadStreamAsync(EventStoreTestHost host, Guid streamId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();
        return await unitOfWork.CreateEventStreamRepository(transaction).GetManyAsync(streamId);
    }

    [Fact]
    public async Task A_save_that_fails_before_the_commit_leaves_nothing_for_the_next_attempt_to_write_twice()
    {
        await using var host = CreateHost();
        var streamId = await CreateStreamAsync(host);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = (IEventSource)ActivatorUtilities.CreateInstance(scope.ServiceProvider, typeof(EventSource), new FailingOnceDurableHandover());

            await events.AppendAsync<FailedBatchProbe>(streamId, new FailedBatchProbeTouched(2));
            await Assert.ThrowsAsync<InvalidOperationException>(() => events.SaveChangesAsync());

            await events.AppendAsync<FailedBatchProbe>(streamId, new FailedBatchProbeTouched(2));
            await events.SaveChangesAsync();
        }

        var stream = await ReadStreamAsync(host, streamId);
        Assert.Equal([1L, 2L], stream.Select(e => e.Version));
    }

    [Fact]
    public async Task A_save_refused_before_anything_is_written_does_not_refuse_the_next_one()
    {
        await using var host = CreateHost();
        var streamId = await CreateStreamAsync(host);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();

            host.Session.Set(TestSessionContext.ForTenant(EventStoreTestHost.DefaultTenantId) with { CausationId = null });
            await events.AppendAsync<FailedBatchProbe>(streamId, new FailedBatchProbeTouched(2));
            await Assert.ThrowsAsync<InvalidOperationException>(() => events.SaveChangesAsync());

            host.Session.Set(TestSessionContext.ForTenant(EventStoreTestHost.DefaultTenantId));
            await events.AppendAsync<FailedBatchProbe>(streamId, new FailedBatchProbeTouched(2));
            await events.SaveChangesAsync();
        }

        var stream = await ReadStreamAsync(host, streamId);
        Assert.Equal([1L, 2L], stream.Select(e => e.Version));
    }

    [Fact]
    public async Task A_stated_subject_whose_append_failed_is_not_left_behind_for_the_same_event()
    {
        await using var host = CreateHost();
        var streamId = await CreateStreamAsync(host);
        var touched = new FailedBatchProbeTouched(2);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();

            host.Session.Clear();
            await Assert.ThrowsAsync<SessionRequiredException>(() =>
                events.AppendOnBehalfOfAsync<FailedBatchProbe>(streamId, touched, new EventSubject(Guid.NewGuid())));

            host.Session.Set(TestSessionContext.ForTenant(EventStoreTestHost.DefaultTenantId));
            await events.AppendAsync<FailedBatchProbe>(streamId, touched);
            await events.SaveChangesAsync();
        }

        var stream = await ReadStreamAsync(host, streamId);
        Assert.Equal([1L, 2L], stream.Select(e => e.Version));
        Assert.All(stream, e => Assert.Equal(EventStoreTestHost.DefaultTenantId, e.TenantId));
    }
}
