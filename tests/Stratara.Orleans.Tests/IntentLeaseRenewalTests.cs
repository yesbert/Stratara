using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.Outbox;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A lease renews its hand-over from a timer of its own, not from the scheduler the handler runs on: a handler
/// that blocks its scheduler — the activation's, in a grain — past the grace is still renewed and never claimed
/// by the drain.
/// </summary>
public sealed class IntentLeaseRenewalTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(700);

    [Fact]
    public async Task A_lease_renews_while_the_scheduler_it_was_started_on_is_blocked()
    {
        var intents = new Mock<ICommandIntentStore>();
        var renewals = 0;
        intents.Setup(store => store.RenewAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref renewals))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection()
            .AddSingleton(intents.Object)
            .AddSingleton(TimeProvider.System)
            .AddSingleton(Options.Create(new OrleansDispatchOptions { IntentGrace = Grace }))
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();

        using var scheduler = new SingleThreadScheduler();
        var renewalsWhileBlocked = await Task.Factory.StartNew(
            async () =>
            {
                await using var lease = await IntentLease.StartAsync(services, Guid.NewGuid(), claimedAt: null);
                var before = Volatile.Read(ref renewals);
#pragma warning disable S2925 // Blocking the scheduler's only thread is what the test measures; an await would free it.
                Thread.Sleep(Blocked);
#pragma warning restore S2925
                return Volatile.Read(ref renewals) - before;
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap();

        Assert.True(renewalsWhileBlocked >= 2, $"only {renewalsWhileBlocked} renewals ran while the scheduler was blocked for {Blocked} under a grace of {Grace}");
    }

    /// <summary>One thread, one queue: what a grain activation's scheduler is to the code running on it.</summary>
    private sealed class SingleThreadScheduler : TaskScheduler, IDisposable
    {
        private readonly BlockingCollection<Task> _queue = [];
        private readonly Thread _thread;

        public SingleThreadScheduler()
        {
            _thread = new Thread(() =>
            {
                foreach (var task in _queue.GetConsumingEnumerable())
                {
                    TryExecuteTask(task);
                }
            })
            {
                IsBackground = true,
            };
            _thread.Start();
        }

        public override int MaximumConcurrencyLevel => 1;

        public void Dispose() => _queue.CompleteAdding();

        protected override void QueueTask(Task task) => _queue.Add(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();
    }
}
