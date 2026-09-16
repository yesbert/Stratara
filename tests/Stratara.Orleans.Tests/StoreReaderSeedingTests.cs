using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The reader's head and the seeding that uses it: the default head walks the partition to its last position, and a
/// seeding writes the head for every registered consumer and partition without a checkpoint, leaves the ones that
/// have one, and reports both.
/// </summary>
public sealed class StoreReaderSeedingTests
{
    [Fact]
    public async Task The_default_head_walks_the_partition_to_its_last_position_and_is_zero_when_empty()
    {
        ICommittedPositionReader reader = new WalkedReader(entriesPerPartition: new Dictionary<int, int> { [0] = 2_345, [1] = 0 });

        Assert.Equal(2_345, await reader.HeadAsync(0));
        Assert.Equal(0, await reader.HeadAsync(1));
    }

    [Fact]
    public async Task A_seeding_writes_the_head_where_no_checkpoint_exists_and_leaves_the_ones_that_do()
    {
        var checkpoints = new MemoryCheckpoints();
        await checkpoints.SetAsync("Orders", 0, "head/2", 17);
        var reader = new HeadReader("head/2", heads: [100, 200]);
        var seeding = new StoreReaderSeeding(
            [new Consumers("Orders"), new Consumers("Invoices", "sagas")],
            reader,
            checkpoints,
            Options.Create(new CommitOrderOptions { PartitionCount = 2 }));

        var report = await seeding.SeedAtHeadAsync();

        Assert.Equal(new StoreReaderSeedingReport(Seeded: 5, Existing: 1), report);
        Assert.Equal(17, await checkpoints.GetAsync("Orders", 0, "head/2"));
        Assert.Equal(200, await checkpoints.GetAsync("Orders", 1, "head/2"));
        Assert.Equal(100, await checkpoints.GetAsync("Invoices", 0, "head/2"));
        Assert.Equal(200, await checkpoints.GetAsync("sagas", 1, "head/2"));
        Assert.Equal(["Invoices", "Orders", "sagas"], checkpoints.Consumers);
    }

    [Fact]
    public async Task A_seeding_refuses_a_checkpoint_written_under_another_reader()
    {
        var checkpoints = new MemoryCheckpoints();
        await checkpoints.SetAsync("Orders", 0, "other/2", 17);
        var seeding = new StoreReaderSeeding([new Consumers("Orders")], new HeadReader("head/2", heads: [1, 1]), checkpoints, Options.Create(new CommitOrderOptions { PartitionCount = 2 }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeding.SeedAtHeadAsync());
    }

    private sealed class Consumers(params string[] names) : INudgeTarget
    {
        public IReadOnlyList<string> ConsumerNames { get; } = names;

        public Task NudgeAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;

        public Task EnsureRunningAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;
    }

    /// <summary>Answers the head per partition directly, as the shipped readers do.</summary>
    private sealed class HeadReader(string name, long[] heads) : ICommittedPositionReader
    {
        public string Name => name;

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the seeding asks for the head, not for entries");

        public Task<long> HeadAsync(int partition, CancellationToken cancellationToken = default) => Task.FromResult(heads[partition]);
    }

    /// <summary>Implements only the port's required members, so the head is the default's walk.</summary>
    private sealed class WalkedReader(Dictionary<int, int> entriesPerPartition) : ICommittedPositionReader
    {
        public string Name => "walked/2";

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
        {
            var total = entriesPerPartition[partition];
            var entries = Enumerable.Range((int)afterPosition + 1, Math.Max(0, Math.Min(batchSize, total - (int)afterPosition)))
                .Select(position => new CommittedEntry(new EventStreamEntry
                {
                    StreamId = Guid.NewGuid(),
                    Version = 1,
                    EventTypeName = "Probe",
                    AggregateTypeName = "ProbeAggregate",
                    DataJson = "{}",
                    BucketId = 0,
                    TenantId = Guid.NewGuid(),
                    ActorTenantId = Guid.NewGuid(),
                    ActorUserId = Guid.NewGuid(),
                }, position))
                .ToList();
            return Task.FromResult(entries.Count == 0
                ? CommittedBatch.Empty(afterPosition)
                : new CommittedBatch(entries, entries[^1].Position) { HasMore = entries[^1].Position < total });
        }
    }

    private sealed class MemoryCheckpoints : IProjectionCheckpointStore
    {
        private readonly Dictionary<(string, int), (string Reader, long Position)> _rows = [];

        public IReadOnlyList<string> Consumers => [.. _rows.Keys.Select(key => key.Item1).Distinct().Order(StringComparer.Ordinal)];

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default)
        {
            if (!_rows.TryGetValue((projection, partition), out var row))
            {
                return Task.FromResult(0L);
            }

            return row.Reader == reader
                ? Task.FromResult(row.Position)
                : throw new InvalidOperationException($"written by {row.Reader}, read by {reader}");
        }

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            _rows[(projection, partition)] = (reader, position);
            return Task.CompletedTask;
        }
    }
}
