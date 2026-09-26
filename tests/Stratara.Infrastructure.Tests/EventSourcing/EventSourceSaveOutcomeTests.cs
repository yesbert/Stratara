using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Messages;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>event-sourcing-store</c> → a save with nothing staged does nothing, and a save that committed its events but
/// could not hand their bundle on says so with a failure of its own. Against the SQLite test store.
/// </summary>
public class EventSourceSaveOutcomeTests
{
    private sealed class OutcomeProbe
    {
        public int Value { get; set; }
    }

    private sealed record OutcomeProbeTouched(int By);

    /// <summary>The bus and the outbox's own table both fail, as on a host without durable bundles.</summary>
    private sealed class FailingHandover(Exception failure) : IEventBundleOutboxDispatcher
    {
        public FailingHandover()
            : this(new InvalidOperationException("the bus and the outbox table are both unavailable"))
        {
        }

        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default) =>
            throw failure;

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static EventStoreTestHost CreateHost() =>
        EventStoreTestHost.Create(services => services
            .AddTrustedType<OutcomeProbe>()
            .AddTrustedType<OutcomeProbeTouched>());

    [Fact]
    public async Task A_save_with_nothing_staged_publishes_nothing()
    {
        await using var host = CreateHost();

        await host.ExecuteAsync(events => events.SaveChangesAsync());

        Assert.Empty(host.Outbox.Bundles);
    }

    [Fact]
    public async Task A_save_that_committed_but_could_not_hand_its_bundle_on_says_so()
    {
        await using var host = CreateHost();
        var streamId = Guid.CreateVersion7();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = (IEventSource)ActivatorUtilities.CreateInstance(scope.ServiceProvider, typeof(EventSource), new FailingHandover());
            await events.CreateAsync<OutcomeProbe>(streamId, new OutcomeProbeTouched(1));

            var failure = await Assert.ThrowsAsync<CommittedEventsNotPublishedException>(() => events.SaveChangesAsync());

            Assert.Equal([streamId], failure.StreamIds);
            Assert.Equal(1, failure.EventCount);
            Assert.IsType<InvalidOperationException>(failure.InnerException);
        }

        await using var read = host.Services.CreateAsyncScope();
        var unitOfWork = read.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();
        Assert.Single(await unitOfWork.CreateEventStreamRepository(transaction).GetManyAsync(streamId));
    }

    /// <summary>A handover cancelled after the commit leaves the events recorded just the same, so it says so too.</summary>
    [Fact]
    public async Task A_handover_cancelled_after_the_commit_still_says_the_events_are_committed()
    {
        await using var host = CreateHost();
        var streamId = Guid.CreateVersion7();
        await using var scope = host.Services.CreateAsyncScope();
        var events = (IEventSource)ActivatorUtilities.CreateInstance(
            scope.ServiceProvider, typeof(EventSource), new FailingHandover(new OperationCanceledException("the host is stopping")));
        await events.CreateAsync<OutcomeProbe>(streamId, new OutcomeProbeTouched(1));

        var failure = await Assert.ThrowsAsync<CommittedEventsNotPublishedException>(() => events.SaveChangesAsync());

        Assert.IsType<OperationCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task A_save_with_nothing_staged_still_needs_a_session()
    {
        await using var host = CreateHost();
        host.Session.Clear();

        await Assert.ThrowsAsync<SessionRequiredException>(() => host.ExecuteAsync(events => events.SaveChangesAsync()));
        Assert.Empty(host.Outbox.Bundles);
    }

    /// <summary>With durable bundles the bundle was recorded with the commit; a handover that fails afterwards loses nothing.</summary>
    private sealed class DurableFailingHandover : IEventBundleOutboxDispatcher
    {
        public bool StoresBundlesWithCommit => true;

        public Task StoreEventBundleAsync(EventBundle eventBundle, ITransaction transaction, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnqueueEventBundleAsync(EventBundle eventBundle, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException("the host is stopping");

        public Task EnqueueOutboxEntriesAsync(IEnumerable<OutboxEntry> outboxEntries, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task A_handover_that_fails_after_the_commit_on_a_host_with_durable_bundles_does_not_fail_the_save()
    {
        await using var host = CreateHost();
        var streamId = Guid.CreateVersion7();
        await using var scope = host.Services.CreateAsyncScope();
        var events = (IEventSource)ActivatorUtilities.CreateInstance(scope.ServiceProvider, typeof(EventSource), new DurableFailingHandover());
        await events.CreateAsync<OutcomeProbe>(streamId, new OutcomeProbeTouched(1));

        await events.SaveChangesAsync();
    }
}
