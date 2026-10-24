using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Stratara.Abstractions.Outbox;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Scenario <em>Renewals while a burst waits</em>: a silo renews every command it holds in batches, so the store calls
/// per renewal period do not grow with the number held up to the batch size; a batch that fails is logged once and the
/// others are still renewed; a lease registers its command when it starts and releases it when it ends; a command held
/// twice stays renewed until both release it; and the loop starts with the first hold, not with the host.
/// </summary>
public sealed class IntentRenewalsTests
{
    [Fact]
    public async Task Ten_and_a_thousand_held_commands_cost_one_store_call_each()
    {
        foreach (var held in new[] { 10, IntentRenewals.BatchSize })
        {
            var (renewals, store, _) = Build();
            for (var i = 0; i < held; i++)
            {
                renewals.Hold(Guid.NewGuid());
            }

            await renewals.RenewAllAsync(CancellationToken.None);

            store.Verify(s => s.RenewManyAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
            store.Verify(s => s.RenewAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task More_than_a_batch_is_renewed_in_batches_and_a_failed_batch_is_logged_once()
    {
        var (renewals, store, logger) = Build();
        var calls = 0;
        store.Setup(s => s.RenewManyAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref calls) == 1 ? Task.FromException(new InvalidOperationException("the pool is exhausted")) : Task.CompletedTask);
        for (var i = 0; i < IntentRenewals.BatchSize * 2 + 1; i++)
        {
            renewals.Hold(Guid.NewGuid());
        }

        await renewals.RenewAllAsync(CancellationToken.None);

        Assert.Equal(3, calls);
        var failed = Assert.Single(logger.Entries, e => e.EventId.Id == LogEvents.Orleans.IntentBatchRenewalFailed);
        Assert.Equal(LogLevel.Warning, failed.Level);
        Assert.Contains(IntentRenewals.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture), failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_released_command_is_not_renewed()
    {
        var (renewals, store, _) = Build();
        var kept = Guid.NewGuid();
        var released = Guid.NewGuid();
        renewals.Hold(kept);
        renewals.Hold(released);
        renewals.Release(released);

        await renewals.RenewAllAsync(CancellationToken.None);

        store.Verify(s => s.RenewManyAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == kept), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_command_held_twice_stays_renewed_until_both_holds_are_released()
    {
        var (renewals, store, _) = Build();
        var intent = Guid.NewGuid();
        renewals.Hold(intent);
        renewals.Hold(intent);
        renewals.Release(intent);

        await renewals.RenewAllAsync(CancellationToken.None);
        renewals.Release(intent);

        store.Verify(s => s.RenewManyAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == intent), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, renewals.Count);
    }

    [Fact]
    public async Task The_loop_starts_with_the_first_hold_and_renews_on_its_own_period()
    {
        var store = new Mock<ICommandIntentStore>();
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Setup(s => s.RenewManyAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(() => { renewed.TrySetResult(); return Task.CompletedTask; });
        var time = new FakeTimeProvider();
        var services = new ServiceCollection().AddScoped(_ => store.Object).BuildServiceProvider();
        await using var renewals = new IntentRenewals(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.FromSeconds(30) }),
            time,
            new TypedLogger<IntentRenewals>(new RecordingLogger()));

        renewals.Hold(Guid.NewGuid());
        time.Advance(TimeSpan.FromSeconds(10));

        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        store.Verify(s => s.RenewManyAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task A_lease_holds_its_command_with_the_silo_renewals_until_it_ends()
    {
        var store = new Mock<ICommandIntentStore>();
        store.Setup(s => s.TryRenewAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var services = new ServiceCollection()
            .AddSingleton(store.Object)
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .AddSingleton(Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.FromSeconds(30) }))
            .AddLogging()
            .AddSingleton<IntentRenewals>()
            .BuildServiceProvider();
        var renewals = services.GetRequiredService<IntentRenewals>();

        var lease = await IntentLease.StartAsync(services, Guid.NewGuid(), claimedAt: null) ?? throw new InvalidOperationException("the lease was not started");
        Assert.Equal(1, renewals.Count);

        await lease.DisposeAsync();
        Assert.Equal(0, renewals.Count);
    }

    private static (IntentRenewals Renewals, Mock<ICommandIntentStore> Store, RecordingLogger Logger) Build()
    {
        var store = new Mock<ICommandIntentStore>();
        store.Setup(s => s.RenewManyAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var logger = new RecordingLogger();
        var services = new ServiceCollection().AddScoped(_ => store.Object).BuildServiceProvider();
        var renewals = new IntentRenewals(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.FromSeconds(30) }),
            new FakeTimeProvider(),
            new TypedLogger<IntentRenewals>(logger));
        return (renewals, store, logger);
    }
}
