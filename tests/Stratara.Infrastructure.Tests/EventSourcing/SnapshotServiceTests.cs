using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.Reflections;

namespace Stratara.Infrastructure.Tests.EventSourcing;

public class SnapshotServiceTests
{
    private readonly Mock<IAggregationService> _aggregationServiceMock = new();
    private readonly Mock<IEventMapperFactory> _eventMapperFactoryMock = new();
    private readonly Mock<ISecureJsonSerializer> _serializerMock = new();
    private readonly Mock<IWriteUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ITransaction> _transactionMock = new();
    private readonly Mock<ISnapshotRepository> _snapshotRepoMock = new();
    private readonly TrustedTypeResolver _typeResolver = new();
    private readonly SnapshotService _service;

    private readonly Guid _tenantId = Guid.NewGuid();

    public SnapshotServiceTests()
    {
        _typeResolver.Register(typeof(TestAggregate));

        _unitOfWorkMock.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_transactionMock.Object);
        _unitOfWorkMock.Setup(u => u.CreateSnapshotRepository(_transactionMock.Object)).Returns(_snapshotRepoMock.Object);
        _serializerMock.Setup(s => s.SerializeAsync(It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync("{}");

        _service = new SnapshotService(
            _aggregationServiceMock.Object,
            _eventMapperFactoryMock.Object,
            _serializerMock.Object,
            _unitOfWorkMock.Object,
            _typeResolver);
    }

    private sealed class TestAggregate
    {
        public string Name { get; set; } = "";
    }

    private EventStreamEntry CreateEntry(Guid streamId, long version) =>
        new()
        {
            Id = Guid.NewGuid(),
            StreamId = streamId,
            Version = version,
            EventTypeName = "TestEvent",
            AggregateTypeName = typeof(TestAggregate).GetQualifiedTypeName(),
            DataJson = "{}",
            BucketId = 1,
            TenantId = _tenantId,
            ActorTenantId = _tenantId,
            ActorUserId = Guid.NewGuid()
        };

    [Fact]
    public async Task AddSnapshotIfNeeded_BelowThreshold_NoSnapshot()
    {
        var streamId = Guid.NewGuid();
        var entries = Enumerable.Range(1, 10).Select(i => CreateEntry(streamId, i)).ToList();

        _snapshotRepoMock.Setup(r => r.GetLatestVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);

        await _service.AddSnapshotIfNeededAsync(entries);

        _snapshotRepoMock.Verify(r => r.AddAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddSnapshotIfNeeded_AtThreshold_CreatesSnapshot()
    {
        var streamId = Guid.NewGuid();
        var entries = Enumerable.Range(1, 50).Select(i => CreateEntry(streamId, i)).ToList();
        var aggregate = new TestAggregate { Name = "Snapshotted" };

        _snapshotRepoMock.Setup(r => r.GetLatestVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);
        _aggregationServiceMock.Setup(a => a.AggregateAsync(typeof(TestAggregate), streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await _service.AddSnapshotIfNeededAsync(entries);

        _snapshotRepoMock.Verify(r => r.AddAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddSnapshotIfNeeded_AboveThreshold_CreatesSnapshot()
    {
        var streamId = Guid.NewGuid();
        var entries = Enumerable.Range(1, 60).Select(i => CreateEntry(streamId, i)).ToList();
        var aggregate = new TestAggregate { Name = "Snapshotted" };

        _snapshotRepoMock.Setup(r => r.GetLatestVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);
        _aggregationServiceMock.Setup(a => a.AggregateAsync(typeof(TestAggregate), streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await _service.AddSnapshotIfNeededAsync(entries);

        _snapshotRepoMock.Verify(r => r.AddAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddSnapshotIfNeeded_NoExistingSnapshot_CalculatesFromZero()
    {
        var streamId = Guid.NewGuid();
        var entries = Enumerable.Range(1, 50).Select(i => CreateEntry(streamId, i)).ToList();
        var aggregate = new TestAggregate { Name = "Test" };

        _snapshotRepoMock.Setup(r => r.GetLatestVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);
        _aggregationServiceMock.Setup(a => a.AggregateAsync(typeof(TestAggregate), streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await _service.AddSnapshotIfNeededAsync(entries);

        _snapshotRepoMock.Verify(r => r.GetLatestVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>()), Times.Once);
        _snapshotRepoMock.Verify(r => r.AddAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddSnapshotIfNeeded_WithExistingSnapshot_CalculatesFromLast()
    {
        var streamId = Guid.NewGuid();
        var entries = Enumerable.Range(51, 50).Select(i => CreateEntry(streamId, i)).ToList();
        var aggregate = new TestAggregate { Name = "Updated" };

        _snapshotRepoMock.Setup(r => r.GetLatestVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(50L);
        _aggregationServiceMock.Setup(a => a.AggregateAsync(typeof(TestAggregate), streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await _service.AddSnapshotIfNeededAsync(entries);

        _snapshotRepoMock.Verify(r => r.AddAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddSnapshotIfNeeded_MultipleStreams_HandlesEachIndependently()
    {
        var streamId1 = Guid.NewGuid();
        var streamId2 = Guid.NewGuid();

        var entries1 = Enumerable.Range(1, 50).Select(i => CreateEntry(streamId1, i)).ToList();
        var entries2 = Enumerable.Range(1, 10).Select(i => CreateEntry(streamId2, i)).ToList();
        var allEntries = entries1.Concat(entries2).ToList();

        var aggregate = new TestAggregate { Name = "Test" };

        _snapshotRepoMock.Setup(r => r.GetLatestVersionOrDefaultAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(0L);
        _aggregationServiceMock.Setup(a => a.AggregateAsync(typeof(TestAggregate), streamId1, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await _service.AddSnapshotIfNeededAsync(allEntries);

        _snapshotRepoMock.Verify(r => r.AddAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddSnapshotIfNeeded_SerializesAggregateState()
    {
        var streamId = Guid.NewGuid();
        var entries = Enumerable.Range(1, 50).Select(i => CreateEntry(streamId, i)).ToList();
        var aggregate = new TestAggregate { Name = "Serialized" };

        _snapshotRepoMock.Setup(r => r.GetLatestVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);
        _aggregationServiceMock.Setup(a => a.AggregateAsync(typeof(TestAggregate), streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await _service.AddSnapshotIfNeededAsync(entries);

        _serializerMock.Verify(s => s.SerializeAsync(It.IsAny<object>(), _tenantId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddSnapshotIfNeeded_PersistsToRepository()
    {
        var streamId = Guid.NewGuid();
        var entries = Enumerable.Range(1, 50).Select(i => CreateEntry(streamId, i)).ToList();
        var aggregate = new TestAggregate { Name = "Persisted" };

        _snapshotRepoMock.Setup(r => r.GetLatestVersionOrDefaultAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(0L);
        _aggregationServiceMock.Setup(a => a.AggregateAsync(typeof(TestAggregate), streamId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(It.IsAny<IEnumerable<EventStreamEntry>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await _service.AddSnapshotIfNeededAsync(entries);

        _snapshotRepoMock.Verify(r => r.AddAsync(
            It.Is<Snapshot>(s =>
                s.StreamId == streamId &&
                s.Version == 50 &&
                s.TenantId == _tenantId),
            It.IsAny<CancellationToken>()), Times.Once);

        _transactionMock.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
