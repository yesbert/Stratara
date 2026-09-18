using Moq;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Sagas;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A saga reader's starting position and what it hands its saga. A saga without a checkpoint starts at the checkpoint
/// the sagas shared before each read with its own, else at the furthest checkpoint another saga of the host holds in
/// the partition, else at the beginning; the host's other sagas without one start there with it; a checkpoint that
/// exists — one at the beginning among them — is kept. A stateless saga is handed only the events it declares.
/// </summary>
public sealed class SagaReaderTests
{
    private const string Reader = "scripted/4";
    private const int Partition = 2;
    private static readonly string Billing = SagaReaderGrain.ConsumerOf("BillingSaga");
    private static readonly string Email = SagaReaderGrain.ConsumerOf("EmailSaga");
    private static readonly string Audit = SagaReaderGrain.ConsumerOf("AuditSaga");

    [Fact]
    public async Task A_saga_without_a_checkpoint_starts_at_the_checkpoint_the_sagas_shared()
    {
        var checkpoints = new MemoryCheckpoints();
        await checkpoints.SetAsync(SagaGrain.ConsumerName, Partition, Reader, 40);

        await SagaStart.EnsureAsync(checkpoints, Reader, Partition, Billing, [Billing, Email], TestContext.Current.CancellationToken);

        Assert.Equal(40, await checkpoints.GetAsync(Billing, Partition, Reader));
        Assert.Equal(40, await checkpoints.GetAsync(Email, Partition, Reader));
        Assert.Equal(40, await checkpoints.GetAsync(SagaGrain.ConsumerName, Partition, Reader));
    }

    [Fact]
    public async Task A_saga_without_a_checkpoint_starts_at_the_furthest_checkpoint_another_saga_holds()
    {
        var checkpoints = new MemoryCheckpoints();
        await checkpoints.SetAsync(Billing, Partition, Reader, 12);
        await checkpoints.SetAsync(Email, Partition, Reader, 57);
        await checkpoints.SetAsync(Email, Partition + 1, Reader, 99);

        await SagaStart.EnsureAsync(checkpoints, Reader, Partition, Audit, [Billing, Email, Audit], TestContext.Current.CancellationToken);

        Assert.Equal(57, await checkpoints.GetAsync(Audit, Partition, Reader));
        Assert.Equal(12, await checkpoints.GetAsync(Billing, Partition, Reader));
    }

    [Fact]
    public async Task A_saga_without_a_checkpoint_where_no_saga_has_read_starts_at_the_beginning_and_keeps_it()
    {
        var checkpoints = new MemoryCheckpoints();

        await SagaStart.EnsureAsync(checkpoints, Reader, Partition, Billing, [Billing, Email], TestContext.Current.CancellationToken);

        Assert.True(checkpoints.Has(Billing, Partition));
        Assert.True(checkpoints.Has(Email, Partition));
        Assert.Equal(0, await checkpoints.GetAsync(Billing, Partition, Reader));

        await ((IProjectionCheckpointStore)checkpoints).AdvanceAsync(Email, Partition, Reader, 0, 30);
        await SagaStart.EnsureAsync(checkpoints, Reader, Partition, Billing, [Billing, Email], TestContext.Current.CancellationToken);

        Assert.Equal(0, await checkpoints.GetAsync(Billing, Partition, Reader));
    }

    [Fact]
    public async Task A_saga_whose_reader_starts_after_another_has_read_on_starts_where_that_one_started()
    {
        var checkpoints = new MemoryCheckpoints();
        await SagaStart.EnsureAsync(checkpoints, Reader, Partition, Billing, [Billing, Email], TestContext.Current.CancellationToken);
        await ((IProjectionCheckpointStore)checkpoints).AdvanceAsync(Billing, Partition, Reader, 0, 25);

        await SagaStart.EnsureAsync(checkpoints, Reader, Partition, Email, [Billing, Email], TestContext.Current.CancellationToken);

        Assert.Equal(0, await checkpoints.GetAsync(Email, Partition, Reader));
    }

    [Fact]
    public async Task A_saga_that_has_a_checkpoint_keeps_it()
    {
        var checkpoints = new MemoryCheckpoints();
        await checkpoints.SetAsync(SagaGrain.ConsumerName, Partition, Reader, 40);
        await checkpoints.SetAsync(Billing, Partition, Reader, 45);
        await checkpoints.SetAsync(Email, Partition, Reader, 90);

        await SagaStart.EnsureAsync(checkpoints, Reader, Partition, Billing, [Billing, Email], TestContext.Current.CancellationToken);

        Assert.Equal(45, await checkpoints.GetAsync(Billing, Partition, Reader));
    }

    [Fact]
    public async Task A_saga_with_a_checkpoint_gives_a_saga_added_beside_it_one_where_the_host_s_sagas_read()
    {
        var checkpoints = new MemoryCheckpoints();
        await checkpoints.SetAsync(Billing, Partition, Reader, 45);

        await SagaStart.EnsureAsync(checkpoints, Reader, Partition, Billing, [Billing, Audit], TestContext.Current.CancellationToken);

        Assert.Equal(45, await checkpoints.GetAsync(Audit, Partition, Reader));
        Assert.Equal(45, await checkpoints.GetAsync(Billing, Partition, Reader));
    }

    [Fact]
    public async Task A_stateless_saga_is_handed_only_the_events_it_declares_and_nothing_for_an_entry_without_one()
    {
        var saga = new Mock<ISaga>().Object;
        var handler = new Mock<ISagaHandler>();
        handler.Setup(h => h.GetRelevantEventTypeNames(saga)).Returns(["Billed"]);
        var run = new SagaReaderRun(handler.Object, saga, new Mock<IGrainFactory>(MockBehavior.Strict).Object);
        var entry = new EventStreamEntry
        {
            StreamId = Guid.NewGuid(),
            Version = 1,
            EventTypeName = "Shipped",
            AggregateTypeName = "Order",
            DataJson = "{}",
            BucketId = 0,
            TenantId = Guid.NewGuid(),
            ActorTenantId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(),
        };

        await run.ApplyAsync(entry, [Event("Shipped")], TestContext.Current.CancellationToken);
        handler.Verify(h => h.HandleAsync(It.IsAny<ISaga>(), It.IsAny<IReadOnlyList<IEvent>>(), It.IsAny<CancellationToken>()), Times.Never);

        await run.ApplyAsync(entry, [Event("Shipped"), Event("Billed")], TestContext.Current.CancellationToken);
        handler.Verify(h => h.HandleAsync(saga, It.Is<IReadOnlyList<IEvent>>(events => events.Count == 1 && events[0].EventTypeName == "Billed"), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IEvent Event(string typeName)
    {
        var @event = new Mock<IEvent>();
        @event.SetupGet(e => e.EventTypeName).Returns(typeName);
        return @event.Object;
    }

    /// <summary>A store that tells a missing checkpoint from one at the beginning and creates a first one only where none exists, as the framework's does.</summary>
    private sealed class MemoryCheckpoints : IProjectionCheckpointStore, IFirstCheckpointStore
    {
        private readonly Dictionary<(string, int), long> _rows = [];

        public bool Has(string consumer, int partition) => _rows.ContainsKey((consumer, partition));

        public Task<long> GetAsync(string projection, int partition, string reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.GetValueOrDefault((projection, partition)));

        public Task SetAsync(string projection, int partition, string reader, long position, CancellationToken cancellationToken = default)
        {
            _rows[(projection, partition)] = position;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string consumer, int partition, CancellationToken cancellationToken) => Task.FromResult(Has(consumer, partition));

        public Task<bool> CreateAsync(string consumer, int partition, string reader, long position, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryAdd((consumer, partition), position));
    }
}
