using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>Requests the save's cancellation the moment the store begins to commit, as a stopping host would.</summary>
    private sealed class CancelsWhenTheCommitBegins(CancellationTokenSource stop) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            await stop.CancelAsync();
            return result;
        }
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

    /// <summary>
    /// The save's cancellation is requested while the store commits — the host stops at that moment. The save is not
    /// reported as cancelled, which would have a transport run the committed work again: it completes, or says its
    /// events are committed. On the SQLite test store, which would otherwise abandon the commit.
    /// </summary>
    [Fact]
    public async Task A_save_cancelled_while_the_store_commits_is_not_reported_as_cancelled()
    {
        using var stop = new CancellationTokenSource();
        await using var host = EventStoreTestHost.Create(services => services
            .AddTrustedType<OutcomeProbe>()
            .AddTrustedType<OutcomeProbeTouched>()
            .ConfigureDbContext<StrataraTestWriteDbContext>(options => options.AddInterceptors(new CancelsWhenTheCommitBegins(stop))));
        var streamId = Guid.CreateVersion7();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<OutcomeProbe>(streamId, new OutcomeProbeTouched(1));

            var failure = await Record.ExceptionAsync(() => events.SaveChangesAsync(stop.Token));

            Assert.True(stop.IsCancellationRequested);
            Assert.True(failure is null or CommittedEventsNotPublishedException, $"The save reported {failure?.GetType().Name}.");
        }

        await using var read = host.Services.CreateAsyncScope();
        var store = read.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await store.StartAsync();
        Assert.Single(await store.CreateEventStreamRepository(transaction).GetManyAsync(streamId));
    }

    /// <summary>A save cancelled before it commits is cancelled, and writes no events.</summary>
    [Fact]
    public async Task A_save_cancelled_before_it_commits_writes_no_events()
    {
        await using var host = CreateHost();
        var streamId = Guid.CreateVersion7();
        using var stop = new CancellationTokenSource();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<OutcomeProbe>(streamId, new OutcomeProbeTouched(1));
            await stop.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.SaveChangesAsync(stop.Token));
        }

        await using var read = host.Services.CreateAsyncScope();
        var store = read.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await store.StartAsync();
        Assert.Empty(await store.CreateEventStreamRepository(transaction).GetManyAsync(streamId));
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
        public bool Stored { get; private set; }

        public bool StoresBundlesWithCommit => true;

        public Task StoreEventBundleAsync(EventBundle eventBundle, ITransaction transaction, CancellationToken cancellationToken = default)
        {
            Stored = true;
            return Task.CompletedTask;
        }

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
        var handover = new DurableFailingHandover();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = (IEventSource)ActivatorUtilities.CreateInstance(scope.ServiceProvider, typeof(EventSource), handover);
            await events.CreateAsync<OutcomeProbe>(streamId, new OutcomeProbeTouched(1));

            await events.SaveChangesAsync();
        }

        Assert.True(handover.Stored);

        await using var read = host.Services.CreateAsyncScope();
        var unitOfWork = read.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();
        Assert.Single(await unitOfWork.CreateEventStreamRepository(transaction).GetManyAsync(streamId));
    }
}
