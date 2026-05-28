using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.Tests.EventSourcing;

public class EventChainServiceTests
{
    private readonly Mock<IWriteUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ITransaction> _transactionMock = new();
    private readonly Mock<IEventStreamRepository> _eventStreamRepoMock = new();
    private readonly Mock<IEventChainRepository> _chainRepoMock = new();
    private readonly EventChainService _service;

    public EventChainServiceTests()
    {
        _transactionMock
            .Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _transactionMock
            .Setup(t => t.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _unitOfWorkMock
            .Setup(u => u.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_transactionMock.Object);
        _unitOfWorkMock
            .Setup(u => u.CreateEventStreamRepository(_transactionMock.Object))
            .Returns(_eventStreamRepoMock.Object);
        _unitOfWorkMock
            .Setup(u => u.CreateEventChainRepository(_transactionMock.Object))
            .Returns(_chainRepoMock.Object);

        _service = new EventChainService(_unitOfWorkMock.Object);
    }

    [Fact]
    public async Task AddAnchorIfNeededAsync_NoHashedEvents_DoesNotAddAnchor()
    {
        _eventStreamRepoMock
            .Setup(r => r.GetLastHashedEventAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((EventStreamEntry?)null);

        await _service.AddAnchorIfNeededAsync();

        _chainRepoMock.Verify(
            c => c.AddAnchorAsync(It.IsAny<EventChainAnchor>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AddAnchorIfNeededAsync_SequenceBelowRange_DoesNotAddAnchor()
    {
        var entry = CreateHashedEntry(sequenceNumber: 3);
        _eventStreamRepoMock
            .Setup(r => r.GetLastHashedEventAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);

        _chainRepoMock
            .Setup(c => c.GetLastSequenceNumberOrDefaultAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _service.AddAnchorIfNeededAsync();

        _chainRepoMock.Verify(
            c => c.AddAnchorAsync(It.IsAny<EventChainAnchor>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AddAnchorIfNeededAsync_SequenceAtRange_AddsAnchor()
    {
        var entry = CreateHashedEntry(sequenceNumber: 10);
        _eventStreamRepoMock
            .Setup(r => r.GetLastHashedEventAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);

        _chainRepoMock
            .Setup(c => c.GetLastSequenceNumberOrDefaultAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _chainRepoMock
            .Setup(c => c.AddAnchorAsync(It.IsAny<EventChainAnchor>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _service.AddAnchorIfNeededAsync();

        _chainRepoMock.Verify(
            c => c.AddAnchorAsync(It.IsAny<EventChainAnchor>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _transactionMock.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddAnchorIfNeededAsync_AddsAnchorWithCorrectData()
    {
        var tenantId = Guid.NewGuid();
        var bucketId = 42;
        var hashBytes = "test-hash"u8.ToArray();
        var entry = CreateHashedEntry(sequenceNumber: 10, tenantId: tenantId, bucketId: bucketId, hash: hashBytes);

        _eventStreamRepoMock
            .Setup(r => r.GetLastHashedEventAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entry);

        _chainRepoMock
            .Setup(c => c.GetLastSequenceNumberOrDefaultAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        EventChainAnchor? capturedAnchor = null;
        _chainRepoMock
            .Setup(c => c.AddAnchorAsync(It.IsAny<EventChainAnchor>(), It.IsAny<CancellationToken>()))
            .Callback<EventChainAnchor, CancellationToken>((a, _) => capturedAnchor = a)
            .Returns(Task.CompletedTask);

        await _service.AddAnchorIfNeededAsync();

        Assert.NotNull(capturedAnchor);
        Assert.Equal(10, capturedAnchor!.SequenceNumber);
        Assert.Equal(hashBytes, capturedAnchor.AnchorHash);
        Assert.Equal(tenantId, capturedAnchor.TenantId);
        Assert.Equal(bucketId, capturedAnchor.BucketId);
    }

    private static EventStreamEntry CreateHashedEntry(
        long sequenceNumber = 1,
        Guid? tenantId = null,
        int bucketId = 1,
        byte[]? hash = null)
    {
        var tid = tenantId ?? Guid.NewGuid();
        return new EventStreamEntry
        {
            Id = Guid.NewGuid(),
            StreamId = Guid.NewGuid(),
            SequenceNumber = sequenceNumber,
            TenantId = tid,
            BucketId = bucketId,
            Hash = hash ?? [0x01, 0x02],
            Version = 1,
            EventTypeName = "TestEvent",
            AggregateTypeName = "TestAggregate",
            DataJson = "{}",
            ActorTenantId = tid,
            ActorUserId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}
