using Microsoft.Extensions.DependencyInjection;
using Polly;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.Projections;

namespace Stratara.Orleans.Tests;

/// <summary>
/// The token a catch-up receives reaches the store: the reader and the checkpoint store see it, and a
/// catch-up cancelled while it works stops at the next batch boundary without reading or writing again.
/// </summary>
public sealed class StoreReaderCancellationTests
{
    [Fact]
    public async Task A_cancelled_catch_up_stops_between_batches_without_another_checkpoint_write()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new EndlessReader();
        var checkpoints = new CountingCheckpoints(onWrite: cancellation.Cancel);
        var loop = LoopOver(reader, checkpoints);

        var applied = await loop.CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count), static () => false, cancellation.Token);

        Assert.Equal(10, applied);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(1, checkpoints.Writes);
        Assert.True(reader.SawToken && checkpoints.SawToken, "the token did not reach the reader and the checkpoint store");
    }

    [Fact]
    public async Task A_catch_up_whose_token_is_already_cancelled_neither_reads_nor_writes()
    {
        var reader = new EndlessReader();
        var checkpoints = new CountingCheckpoints(onWrite: static () => { });
        var loop = LoopOver(reader, checkpoints);

        var applied = await loop.CatchUpAsync((batch, _) => Task.FromResult(batch.Entries.Count), static () => false, new CancellationToken(canceled: true));

        Assert.Equal(0, applied);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, checkpoints.Writes);
    }

    private static StoreReaderLoop LoopOver(ICommittedPositionReader reader, IProjectionCheckpointStore checkpoints)
    {
        var services = new ServiceCollection().AddSingleton(reader).AddSingleton(checkpoints).BuildServiceProvider();
        return new StoreReaderLoop(services.GetRequiredService<IServiceScopeFactory>(), ResiliencePipeline.Empty, "View", 0, 10, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    /// <summary>Always has a full batch and more after it.</summary>
    private sealed class EndlessReader : ICommittedPositionReader
    {
        public string Name => "endless/16";

        public int Reads { get; private set; }

        public bool SawToken { get; private set; }

        public Task<CommittedBatch> ReadAfterAsync(int partition, long afterPosition, int batchSize, CancellationToken cancellationToken = default)
        {
            Reads++;
            SawToken |= cancellationToken.CanBeCanceled;
            var entries = Enumerable.Range(1, batchSize)
                .Select(offset => new CommittedEntry(new EventStreamEntry
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
                }, afterPosition + offset))
                .ToList();
            return Task.FromResult(new CommittedBatch(entries, entries[^1].Position) { HasMore = true });
        }
    }

    private sealed class CountingCheckpoints(Action onWrite) : IProjectionCheckpointStore
    {
        public int Writes { get; private set; }

        public bool SawToken { get; private set; }

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default)
        {
            SawToken |= cancellationToken.CanBeCanceled;
            return Task.FromResult(0L);
        }

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            SawToken |= cancellationToken.CanBeCanceled;
            Writes++;
            onWrite();
            return Task.CompletedTask;
        }
    }
}
