using Microsoft.Extensions.DependencyInjection;
using Polly;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The catch-up loop keys its checkpoint on the reader's name, not its class, ends a catch-up after a batch that said
/// nothing more was there instead of issuing an empty read, and resumes a partly applied batch below a transaction
/// whose second entry failed, so the transaction's applied entry is applied again.
/// </summary>
public sealed class StoreReaderLoopTests
{
    private const string Consumer = "View";
    private const int Partition = 3;

    [Fact]
    public async Task A_catch_up_over_fewer_entries_than_a_batch_reads_the_store_once()
    {
        var reader = new ScriptedReader("scripted/16", Entries(1, 3));
        var loop = LoopOver(reader, new MemoryCheckpoints(), batchSize: 10);

        var applied = await loop.CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count));

        Assert.Equal(3, applied);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task A_cut_batch_is_followed_by_the_next_read_and_the_last_one_ends_the_catch_up()
    {
        var reader = new ScriptedReader("scripted/16", Entries(1, 25));
        var loop = LoopOver(reader, new MemoryCheckpoints(), batchSize: 10);

        var applied = await loop.CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count));

        Assert.Equal(25, applied);
        Assert.Equal(3, reader.Reads);
    }

    [Fact]
    public async Task A_renamed_reader_class_resumes_from_the_checkpoint_its_predecessor_wrote()
    {
        var checkpoints = new MemoryCheckpoints();
        var first = new ScriptedReader("scripted/16", Entries(1, 4));
        await LoopOver(first, checkpoints, batchSize: 10).CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count));

        var renamed = new RenamedScriptedReader("scripted/16", Entries(1, 6));
        var applied = await LoopOver(renamed, checkpoints, batchSize: 10).CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count));

        Assert.Equal(4, renamed.FirstReadAfter);
        Assert.Equal(2, applied);
    }

    [Fact]
    public async Task A_reader_under_another_partition_count_is_refused_by_the_checkpoint()
    {
        var checkpoints = new MemoryCheckpoints();
        await LoopOver(new ScriptedReader("scripted/16", Entries(1, 2)), checkpoints, batchSize: 10)
            .CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LoopOver(new ScriptedReader("scripted/32", Entries(1, 2)), checkpoints, batchSize: 10)
                .CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count)));
    }

    [Fact]
    public async Task A_refused_advance_is_followed_by_a_catch_up_from_the_position_the_store_holds()
    {
        var checkpoints = new GuardedCheckpoints();
        var loop = LoopOver(new ScriptedReader("scripted/16", Entries(1, 6)), checkpoints, batchSize: 2);
        var applied = new List<long>();
        Task<int> Apply(CommittedBatch batch, CancellationToken _)
        {
            applied.AddRange(batch.Entries.Select(entry => entry.Position));
            return Task.FromResult(batch.Entries.Count);
        }

        checkpoints.AdvancedElsewhereTo = 4;
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.CatchUpAsync(Apply));
        applied.Clear();
        await loop.CatchUpAsync(Apply);

        Assert.Equal([5, 6], applied);
        Assert.Equal(6, await checkpoints.GetAsync(Consumer, Partition, "scripted/16"));
    }

    [Fact]
    public async Task A_projection_that_throws_on_the_second_entry_of_one_transaction_resumes_below_the_transaction()
    {
        var checkpoints = new MemoryCheckpoints();
        var entries = Entries(1, 4);
        entries[2] = new CommittedEntry(entries[2].Entry, 2);
        entries[3] = new CommittedEntry(entries[3].Entry, 3);
        var reader = new ScriptedReader("scripted/16", entries);
        var loop = LoopOver(reader, checkpoints, batchSize: 10);
        var failing = entries[2].Entry;
        var applied = new List<CommittedEntry>();
        Task<int> Apply(CommittedBatch batch, CancellationToken _)
        {
            var count = 0;
            foreach (var entry in batch.Entries)
            {
                if (ReferenceEquals(entry.Entry, failing))
                {
                    break;
                }

                applied.Add(entry);
                count++;
            }

            return Task.FromResult(count);
        }

        await loop.CatchUpAsync(Apply);

        Assert.Equal(1, await checkpoints.GetAsync(Consumer, Partition, "scripted/16"));
        failing = null;
        applied.Clear();
        await loop.CatchUpAsync(Apply);

        Assert.Equal([entries[1].Entry, entries[2].Entry, entries[3].Entry], applied.Select(entry => entry.Entry));
        Assert.Equal(3, await checkpoints.GetAsync(Consumer, Partition, "scripted/16"));
    }

    /// <summary>
    /// Change let-a-paused-reader-always-come-back: a resume that forgets the cached position while the loop is reading
    /// the checkpoint must not be undone when that read returns — the read may be of the position the resume forgot, and
    /// on a quiet partition nothing else would ever read the checkpoint again.
    /// </summary>
    [Fact]
    public async Task An_invalidation_during_a_checkpoint_read_is_not_overwritten_by_the_read()
    {
        var checkpoints = new GatedCheckpoints { Position = 5 };
        var loop = LoopOver(new ScriptedReader("scripted/16", Entries(1, 5)), checkpoints, batchSize: 10);

        var first = loop.CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count));
        await checkpoints.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        loop.Invalidate();
        checkpoints.Position = 0;
        checkpoints.Release.SetResult(5);
        Assert.Equal(0, await first);

        var second = await loop.CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count));

        Assert.Equal(5, second);
        Assert.Equal(2, checkpoints.Reads);
    }

    /// <summary>
    /// A saga step whose save committed but could not publish has done its work: the reader counts the entry as applied
    /// and goes on, rather than stalling on it and running the step again.
    /// </summary>
    [Fact]
    public async Task An_entry_whose_handler_committed_but_could_not_publish_counts_as_applied()
    {
        var loop = LoopOver(new ScriptedReader("scripted/16", Entries(1, 3)), new MemoryCheckpoints(), batchSize: 10);
        var entries = Entries(1, 3);
        var attempts = new List<Guid>();

        var applied = await loop.ApplyEachAsync(new CommittedBatch(entries, 3), (entry, _) =>
        {
            attempts.Add(entry.Id);
            return entry.Id == entries[1].Entry.Id
                ? throw new CommittedEventsNotPublishedException([entry.StreamId], 1, new InvalidOperationException("the bus is down"))
                : Task.CompletedTask;
        });

        Assert.Equal(3, applied);
        Assert.Equal(entries.Select(e => e.Entry.Id), attempts);
    }

    private static StoreReaderLoop LoopOver(ICommittedPositionReader reader, IProjectionCheckpointStore checkpoints, int batchSize)
    {
        var services = new ServiceCollection()
            .AddSingleton(reader)
            .AddSingleton(checkpoints)
            .BuildServiceProvider();
        return new StoreReaderLoop(services.GetRequiredService<IServiceScopeFactory>(), ResiliencePipeline.Empty, Consumer, Partition, batchSize, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    private static List<CommittedEntry> Entries(long from, long to) =>
    [
        .. Enumerable.Range((int)from, (int)(to - from + 1)).Select(position => new CommittedEntry(new EventStreamEntry
        {
            StreamId = Guid.NewGuid(),
            Version = 1,
            EventTypeName = "Probe",
            AggregateTypeName = "ProbeAggregate",
            DataJson = "{}",
            BucketId = Partition,
            TenantId = Guid.NewGuid(),
            ActorTenantId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(),
        }, position))
    ];

    private class ScriptedReader(string name, IReadOnlyList<CommittedEntry> entries) : ICommittedPositionReader
    {
        public string Name => name;

        public int Reads { get; private set; }

        public long? FirstReadAfter { get; private set; }

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
        {
            Reads++;
            FirstReadAfter ??= afterPosition;
            var after = entries.Where(entry => entry.Position > afterPosition).ToList();
            if (after.Count == 0)
            {
                return Task.FromResult(CommittedBatch.Empty(afterPosition));
            }

            var batch = after.Take(batchSize).ToList();
            return Task.FromResult(new CommittedBatch(batch, batch[^1].Position) { HasMore = after.Count > batchSize });
        }
    }

    private sealed class RenamedScriptedReader(string name, IReadOnlyList<CommittedEntry> entries) : ScriptedReader(name, entries);

    private sealed class MemoryCheckpoints : IProjectionCheckpointStore
    {
        private readonly Dictionary<(string, int), (string Reader, long Position)> _stored = new();

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default)
        {
            if (!_stored.TryGetValue((projection, partition), out var checkpoint))
            {
                return Task.FromResult(0L);
            }

            return checkpoint.Reader == reader
                ? Task.FromResult(checkpoint.Position)
                : throw new InvalidOperationException($"written by '{checkpoint.Reader}', not '{reader}'");
        }

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            _stored[(projection, partition)] = (reader, position);
            return Task.CompletedTask;
        }
    }

    /// <summary>A store whose first read waits for <see cref="Release"/> and returns what it is given; later reads return <see cref="Position"/>.</summary>
    private sealed class GatedCheckpoints : IProjectionCheckpointStore
    {
        public long Position { get; set; }

        public int Reads { get; private set; }

        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<long> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Reads > 1)
            {
                return Task.FromResult(Position);
            }

            Reading.SetResult();
            return Release.Task;
        }

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            Position = position;
            return Task.CompletedTask;
        }
    }

    /// <summary>Refuses an advance from a position it no longer holds; <see cref="AdvancedElsewhereTo"/> stands for another activation's write.</summary>
    private sealed class GuardedCheckpoints : IProjectionCheckpointStore
    {
        private long _position;

        public long? AdvancedElsewhereTo { get; set; }

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default) => Task.FromResult(_position);

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            _position = position;
            return Task.CompletedTask;
        }

        public Task AdvanceAsync(string projection, int partition, string reader, long from, long to, CancellationToken cancellationToken = default)
        {
            if (AdvancedElsewhereTo is { } elsewhere)
            {
                _position = elsewhere;
                AdvancedElsewhereTo = null;
            }

            if (_position != from)
            {
                throw new InvalidOperationException($"at {_position}, not at {from}");
            }

            _position = to;
            return Task.CompletedTask;
        }
    }
}
