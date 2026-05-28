using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.Tests.EventSourcing;

public class AggregationServiceTests
{
    private readonly Mock<IWriteUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<IEventMapperFactory> _eventMapperFactoryMock = new();
    private readonly Mock<ISecureJsonSerializer> _serializerMock = new();
    private readonly Mock<ITransaction> _transactionMock = new();
    private readonly Mock<IEventStreamRepository> _eventStreamRepoMock = new();
    private readonly Mock<ISnapshotRepository> _snapshotRepoMock = new();
    private readonly AggregationService _service;

    public AggregationServiceTests()
    {
        _unitOfWorkMock.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_transactionMock.Object);
        _unitOfWorkMock.Setup(u => u.CreateEventStreamRepository(_transactionMock.Object)).Returns(_eventStreamRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.CreateSnapshotRepository(_transactionMock.Object)).Returns(_snapshotRepoMock.Object);
        _service = new AggregationService(_unitOfWorkMock.Object, _eventMapperFactoryMock.Object, _serializerMock.Object);
    }

    private sealed class TestAggregate
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }

        public void Apply(TestCreated e)
        {
            Name = e.Name;
            Value = e.Value;
        }
    }

    private sealed record TestCreated(string Name, int Value);

    [Fact]
    public async Task AggregateAsync_StreamNotFound_ReturnsNull()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await _service.AggregateAsync<TestAggregate>(streamId);

        Assert.Null(result);
    }

    [Fact]
    public async Task AggregateAsync_NoSnapshot_RebuildsFromAllEvents()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _snapshotRepoMock.Setup(r => r.GetAsync(streamId, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Snapshot?)null);

        var entries = new List<EventStreamEntry>();
        _eventStreamRepoMock.Setup(r => r.GetManyAsync(streamId, 0L, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var eventMock = new Mock<IEvent>();
        eventMock.Setup(e => e.Data).Returns(new TestCreated("Test", 42));
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(entries, It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent> { eventMock.Object });

        var result = await _service.AggregateAsync<TestAggregate>(streamId);

        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task AggregateAsync_WithSnapshot_RebuildsFromSnapshotVersion()
    {
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            StreamId = streamId,
            Version = 10,
            AggregateTypeName = typeof(TestAggregate).AssemblyQualifiedName!,
            DataJson = "{}",
            BucketId = 1,
            TenantId = tenantId,
            Timestamp = DateTimeOffset.UtcNow
        };

        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _snapshotRepoMock.Setup(r => r.GetAsync(streamId, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var snapshotAggregate = new TestAggregate { Name = "Snapshotted", Value = 100 };
        _serializerMock.Setup(s => s.DeserializeAsync(snapshot.DataJson, typeof(TestAggregate), tenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshotAggregate);

        var entries = new List<EventStreamEntry>();
        _eventStreamRepoMock.Setup(r => r.GetManyAsync(streamId, 11L, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var events = new List<IEvent>();
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(entries, It.IsAny<CancellationToken>())).ReturnsAsync(events);

        var result = await _service.AggregateAsync<TestAggregate>(streamId);

        Assert.NotNull(result);
        Assert.Equal("Snapshotted", result.Name);
        Assert.Equal(100, result.Value);
    }

    [Fact]
    public async Task AggregateAsync_NoEvents_ReturnsDefaultAggregate()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _snapshotRepoMock.Setup(r => r.GetAsync(streamId, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Snapshot?)null);

        var entries = new List<EventStreamEntry>();
        _eventStreamRepoMock.Setup(r => r.GetManyAsync(streamId, 0L, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(entries, It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        var result = await _service.AggregateAsync<TestAggregate>(streamId);

        Assert.NotNull(result);
        Assert.Equal("", result.Name);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public async Task AggregateAsync_AppliesEventsAfterSnapshot()
    {
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            StreamId = streamId,
            Version = 5,
            AggregateTypeName = typeof(TestAggregate).AssemblyQualifiedName!,
            DataJson = "{}",
            BucketId = 1,
            TenantId = tenantId,
            Timestamp = DateTimeOffset.UtcNow
        };

        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _snapshotRepoMock.Setup(r => r.GetAsync(streamId, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var snapshotAggregate = new TestAggregate { Name = "Old", Value = 1 };
        _serializerMock.Setup(s => s.DeserializeAsync(snapshot.DataJson, typeof(TestAggregate), tenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshotAggregate);

        var entries = new List<EventStreamEntry>();
        _eventStreamRepoMock.Setup(r => r.GetManyAsync(streamId, 6L, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var eventMock = new Mock<IEvent>();
        eventMock.Setup(e => e.Data).Returns(new TestCreated("Updated", 99));
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(entries, It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent> { eventMock.Object });

        var result = await _service.AggregateAsync<TestAggregate>(streamId);

        Assert.NotNull(result);
        Assert.Equal("Updated", result.Name);
        Assert.Equal(99, result.Value);
    }

    [Fact]
    public async Task AggregateAsync_UntypedOverload_ReturnsCorrectType()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _snapshotRepoMock.Setup(r => r.GetAsync(streamId, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Snapshot?)null);

        var entries = new List<EventStreamEntry>();
        _eventStreamRepoMock.Setup(r => r.GetManyAsync(streamId, 0L, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(entries, It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        var result = await _service.AggregateAsync(typeof(TestAggregate), streamId);

        Assert.NotNull(result);
        Assert.IsType<TestAggregate>(result);
    }

    [Fact]
    public async Task AggregateAsync_CreatesAndDisposesTransaction()
    {
        var streamId = Guid.NewGuid();
        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _service.AggregateAsync<TestAggregate>(streamId);

        _unitOfWorkMock.Verify(u => u.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        _transactionMock.Verify(t => t.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task AggregateAsync_SnapshotDeserializationFails_ThrowsInvalidOperationException()
    {
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            StreamId = streamId,
            Version = 10,
            AggregateTypeName = typeof(TestAggregate).AssemblyQualifiedName!,
            DataJson = "{}",
            BucketId = 1,
            TenantId = tenantId,
            Timestamp = DateTimeOffset.UtcNow
        };

        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _snapshotRepoMock.Setup(r => r.GetAsync(streamId, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        _serializerMock.Setup(s => s.DeserializeAsync(snapshot.DataJson, typeof(TestAggregate), tenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        var entries = new List<EventStreamEntry>();
        _eventStreamRepoMock.Setup(r => r.GetManyAsync(streamId, It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(entries, It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.AggregateAsync<TestAggregate>(streamId));
    }

    [Fact]
    public async Task AggregateAsync_QueriesEventsFromSnapshotVersionPlusOne()
    {
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var snapshot = new Snapshot
        {
            Id = Guid.NewGuid(),
            StreamId = streamId,
            Version = 25,
            AggregateTypeName = typeof(TestAggregate).AssemblyQualifiedName!,
            DataJson = "{}",
            BucketId = 1,
            TenantId = tenantId,
            Timestamp = DateTimeOffset.UtcNow
        };

        _eventStreamRepoMock.Setup(r => r.StreamExistsAsync(streamId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _snapshotRepoMock.Setup(r => r.GetAsync(streamId, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var snapshotAggregate = new TestAggregate { Name = "OK", Value = 1 };
        _serializerMock.Setup(s => s.DeserializeAsync(snapshot.DataJson, typeof(TestAggregate), tenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshotAggregate);

        var entries = new List<EventStreamEntry>();
        _eventStreamRepoMock.Setup(r => r.GetManyAsync(streamId, It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);
        _eventMapperFactoryMock.Setup(f => f.MapToEventsAsync(entries, It.IsAny<CancellationToken>())).ReturnsAsync(new List<IEvent>());

        await _service.AggregateAsync<TestAggregate>(streamId);

        _eventStreamRepoMock.Verify(r => r.GetManyAsync(streamId, 26L, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
