using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Orleans.CommitOrder;
using Stratara.Shared.Partitioning;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Projections.Abstractions;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// The scenarios of <em>A host can wait until a projection has applied what was committed</em> on SQLite with the
/// portable reader: a host's own write is in the read model once the wait reached the mark; a projection that is slow,
/// stopped at an entry or suspended by a replay is answered as such at the limit, never as reached, and a replay that
/// ends within the time is waited through; partitions without entries count as reached; a mark of another partition
/// count does not apply; an unknown projection is refused; and a cancelled wait ends without an answer. Scenarios of
/// <em>A host can wait until a projection has applied its own write</em>: a mark of the host's streams constrains only
/// their partition and is reached, with the write applied, while another partition is stopped, which a mark of the
/// whole store still waits for; a mark of streams in partitions with nothing committed is reached at once; and a mark
/// of no stream is refused.
/// </summary>
public sealed class ProjectionProgressTests
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task A_host_waits_for_its_own_write()
    {
        var runs = new Runs();
        await using var host = await CreateAsync(runs);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();
        var accountId = Guid.NewGuid();

        await AppendOpenedAsync(host, accountId);
        var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);
        var result = await progress.WaitAsync(typeof(BalanceProjection), mark, Patient, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionWaitOutcome.Reached, result.Outcome);
        Assert.Empty(result.Behind);
        Assert.True(runs.Balances.ContainsKey(accountId), "the wait answered reached before the projection applied the write");
    }

    [Fact]
    public async Task A_projection_that_does_not_get_there_in_time_is_answered_as_behind()
    {
        var runs = new Runs();
        var gate = new Gate();
        await using var host = await CreateAsync(runs, gate);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();
        try
        {
            await AppendOpenedAsync(host, Guid.NewGuid());
            var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);

            var result = await progress.WaitAsync(nameof(GatedProjection), mark, Short, TestContext.Current.CancellationToken);

            Assert.Equal(ProjectionWaitOutcome.TimedOut, result.Outcome);
            var lag = Assert.Single(result.Behind);
            Assert.Equal(ProjectionWaitOutcome.TimedOut, lag.State);
        }
        finally
        {
            gate.Open();
        }
    }

    [Fact]
    public async Task A_projection_stopped_at_an_entry_is_answered_as_stalled()
    {
        var runs = new Runs();
        var refusals = new Refusals();
        await using var host = await CreateAsync(runs, refusals: refusals);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();
        var refused = Guid.NewGuid();
        refusals.Accounts.TryAdd(refused, true);

        await AppendOpenedAsync(host, refused);
        var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);
        var result = await progress.WaitAsync(nameof(RefusingProjection), mark, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionWaitOutcome.Stalled, result.Outcome);
        Assert.Equal(ProjectionWaitOutcome.Stalled, Assert.Single(result.Behind).State);
    }

    [Fact]
    public async Task A_projection_suspended_by_a_replay_is_answered_as_suspended_and_the_end_of_the_replay_is_waited_through()
    {
        var runs = new Runs();
        await using var host = await CreateAsync(runs);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();
        var replay = host.Services.GetRequiredService<IProjectionReplayState>();
        var accountId = Guid.NewGuid();

        replay.Activate();
        await AppendOpenedAsync(host, accountId);
        var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);
        var suspended = await progress.WaitAsync(typeof(BalanceProjection), mark, Short, TestContext.Current.CancellationToken);
        replay.Deactivate();
        var reached = await progress.WaitAsync(typeof(BalanceProjection), mark, Patient, TestContext.Current.CancellationToken);

        Assert.Equal(ProjectionWaitOutcome.Suspended, suspended.Outcome);
        Assert.Equal(ProjectionWaitOutcome.Suspended, Assert.Single(suspended.Behind).State);
        Assert.True(reached.IsReached);
        Assert.True(runs.Balances.ContainsKey(accountId));
    }

    [Fact]
    public async Task Partitions_without_entries_count_as_reached()
    {
        var runs = new Runs();
        await using var host = await CreateAsync(runs);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();

        var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);
        var result = await progress.WaitAsync(typeof(BalanceProjection), mark, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.All(mark.Positions, position => Assert.Equal(0, position));
        Assert.True(result.IsReached);
    }

    [Fact]
    public async Task A_mark_of_another_partition_count_does_not_apply()
    {
        var runs = new Runs();
        await using var host = await CreateAsync(runs);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();
        await using var scope = host.Services.CreateAsyncScope();
        var readerName = scope.ServiceProvider.GetRequiredService<ICommittedPositionReader>().Name;

        var result = await progress.WaitAsync(typeof(BalanceProjection), new StoreMark("partition-counter/8", [1, 1, 1, 1, 1, 1, 1, 1]), Patient, TestContext.Current.CancellationToken);

        Assert.NotEqual("partition-counter/8", readerName);
        Assert.Equal(ProjectionWaitOutcome.MarkNotApplicable, result.Outcome);
        Assert.Empty(result.Behind);
    }

    [Fact]
    public async Task An_unknown_projection_is_refused_naming_it()
    {
        var runs = new Runs();
        await using var host = await CreateAsync(runs);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();
        var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => progress.WaitAsync("InvoiceProjection", mark, Patient, TestContext.Current.CancellationToken));

        Assert.Contains("InvoiceProjection", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_wait_ends_without_an_answer()
    {
        var runs = new Runs();
        var gate = new Gate();
        await using var host = await CreateAsync(runs, gate);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();
        try
        {
            await AppendOpenedAsync(host, Guid.NewGuid());
            var mark = await progress.MarkAsync(TestContext.Current.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => progress.WaitAsync(nameof(GatedProjection), mark, Patient, cancellation.Token));
        }
        finally
        {
            gate.Open();
        }
    }

    [Fact]
    public async Task A_host_waits_for_its_own_write_while_another_partition_is_stopped()
    {
        var runs = new Runs();
        var refusals = new Refusals();
        await using var host = await CreateAsync(runs, refusals: refusals);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();
        var partitions = host.Services.GetRequiredService<IOptions<CommitOrderOptions>>().Value.PartitionCount;
        var refused = Guid.NewGuid();
        var own = InAnotherPartition(refused, partitions);
        refusals.Accounts.TryAdd(refused, true);

        await AppendOpenedAsync(host, refused);
        await AppendOpenedAsync(host, own);
        var ownMark = await progress.MarkAsync([own], TestContext.Current.CancellationToken);
        var ownWait = await progress.WaitAsync(nameof(RefusingProjection), ownMark, Patient, TestContext.Current.CancellationToken);
        var balanceWait = await progress.WaitAsync(typeof(BalanceProjection), ownMark, Patient, TestContext.Current.CancellationToken);
        var storeMark = await progress.MarkAsync(TestContext.Current.CancellationToken);
        var storeWait = await progress.WaitAsync(nameof(RefusingProjection), storeMark, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.True(ownMark.Positions[PartitionOf(own, partitions)] > 0, "the mark does not constrain the partition of the host's write");
        Assert.All(Enumerable.Range(0, partitions).Where(p => p != PartitionOf(own, partitions)), p => Assert.Equal(0, ownMark.Positions[p]));
        Assert.True(ownWait.IsReached, $"the wait for the host's own write was held up: {ownWait.Outcome}");
        Assert.True(balanceWait.IsReached);
        Assert.True(runs.Balances.ContainsKey(own), "the wait answered reached before the projection applied the host's write");
        Assert.Equal(ProjectionWaitOutcome.Stalled, storeWait.Outcome);
        Assert.Equal(PartitionOf(refused, partitions), Assert.Single(storeWait.Behind).Partition);
    }

    [Fact]
    public async Task A_mark_of_no_stream_is_refused()
    {
        var runs = new Runs();
        await using var host = await CreateAsync(runs);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();

        await Assert.ThrowsAsync<ArgumentException>(() => progress.MarkAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_mark_of_a_stream_in_a_partition_nothing_was_committed_to_is_reached_at_once()
    {
        var runs = new Runs();
        await using var host = await CreateAsync(runs);
        var progress = host.Services.GetRequiredService<IProjectionProgress>();

        var mark = await progress.MarkAsync([Guid.NewGuid()], TestContext.Current.CancellationToken);
        var result = await progress.WaitAsync(typeof(BalanceProjection), mark, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.True(result.IsReached);
    }

    private static int PartitionOf(Guid streamId, int partitions) => PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), partitions);

    private static Guid InAnotherPartition(Guid streamId, int partitions)
    {
        var partition = PartitionOf(streamId, partitions);
        Guid other;
        do
        {
            other = Guid.NewGuid();
        }
        while (PartitionOf(other, partitions) == partition);

        return other;
    }

    private static Task<ExecutionModelTestHost> CreateAsync(Runs runs, Gate? gate = null, Refusals? refusals = null) =>
        ExecutionModelTestHost.CreateAsync(services =>
        {
            services
                .AddSingleton(runs)
                .AddAggregatesFromAssemblyContaining<Account>()
                .AddScoped<IProjection, BalanceProjection>();
            if (gate is not null)
            {
                services.AddSingleton(gate).AddScoped<IProjection, GatedProjection>();
            }

            if (refusals is not null)
            {
                services.AddSingleton(refusals).AddScoped<IProjection, RefusingProjection>();
            }

            services.AddStrataraProjectionGrains();
        });

    private static async Task AppendOpenedAsync(ExecutionModelTestHost host, Guid accountId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Account>(accountId, new AccountOpened(accountId, ExecutionModelTestHost.DefaultTenantId, 1m));
        await events.SaveChangesAsync();
    }

    /// <summary>Holds every projection that waits on it until the test opens it.</summary>
    public sealed class Gate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Opened => _open.Task;

        public void Open() => _open.TrySetResult();
    }

    /// <summary>The accounts <see cref="RefusingProjection"/> fails on, every time.</summary>
    public sealed class Refusals
    {
        public ConcurrentDictionary<Guid, bool> Accounts { get; } = new();
    }

    public sealed class GatedProjection(Gate gate) : IProjection
    {
        public Task HandleAsync(IEvent<AccountOpened> @event, CancellationToken cancellationToken) => gate.Opened.WaitAsync(cancellationToken);
    }

    public sealed class RefusingProjection(Refusals refusals) : IProjection
    {
        public Task HandleAsync(IEvent<AccountOpened> @event, CancellationToken cancellationToken) =>
            refusals.Accounts.ContainsKey(@event.StreamId)
                ? throw new InvalidOperationException($"account {@event.StreamId} is refused")
                : Task.CompletedTask;
    }
}
