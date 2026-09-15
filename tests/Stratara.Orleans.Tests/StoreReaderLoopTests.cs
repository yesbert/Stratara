using Microsoft.Extensions.DependencyInjection;
using Polly;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The catch-up loop keys its checkpoint on the reader's name, not its class, and ends a catch-up
/// after a batch that said nothing more was there instead of issuing an empty read.
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
}
