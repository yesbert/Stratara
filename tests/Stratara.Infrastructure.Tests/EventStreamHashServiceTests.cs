using System.Security.Cryptography;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Shared.EventSourcing;

namespace Stratara.Infrastructure.Tests;

public class EventStreamHashServiceTests
{
    private bool _firstCallDone;

    [Fact]
    public async Task HashEventsAsync_Computes_Hashes_In_Batches_And_Persists()
    {
        // Arrange
        var repo = new Mock<IEventStreamRepository>(MockBehavior.Strict);
        var transaction = new Mock<ITransaction>();
        var uow = new Mock<IWriteUnitOfWork>(MockBehavior.Strict);

        transaction.Setup(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        transaction.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);

        uow.Setup(u => u.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        uow.Setup(u => u.CreateEventStreamRepository(transaction.Object))
            .Returns(repo.Object);

        var streamId = Guid.NewGuid();
        var entriesBatch1 = new List<EventStreamEntry>
        {
            new()
            {
                Id = Guid.NewGuid(),
                StreamId = streamId,
                SequenceNumber = 2,
                Version = 1,
                EventTypeName = "TestCreated",
                AggregateTypeName = "TestAggregate",
                DataJson = "{\"a\":1}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10),
                BucketId = 0,
                TenantId = Guid.Empty,
                ActorTenantId = Guid.Empty,
                ActorUserId = Guid.Empty
            },
            new()
            {
                Id = Guid.NewGuid(),
                StreamId = streamId,
                SequenceNumber = 3,
                Version = 2,
                EventTypeName = "TestUpdated",
                AggregateTypeName = "TestAggregate",
                DataJson = "{\"a\":2}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-9),
                BucketId = 0,
                TenantId = Guid.Empty,
                ActorTenantId = Guid.Empty,
                ActorUserId = Guid.Empty
            }
        };

        var previous = new EventStreamEntry
        {
            Id = Guid.NewGuid(),
            StreamId = streamId,
            SequenceNumber = 1,
            Version = 0,
            EventTypeName = "Genesis",
            AggregateTypeName = "TestAggregate",
            DataJson = "{}",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-11),
            Hash = Convert.FromHexString("AA"),
            BucketId = 0,
            TenantId = Guid.Empty,
            ActorTenantId = Guid.Empty,
            ActorUserId = Guid.Empty
        };

        repo.Setup(r => r.GetUnhashedEventsAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (_firstCallDone)
                {
                    return [];
                }

                _firstCallDone = true;
                return entriesBatch1;
            });

        repo.Setup(r => r.GetPreviousEventAsync(entriesBatch1[0].SequenceNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(previous);

        repo.Setup(r => r.UpdateRange(It.Is<IReadOnlyList<EventStreamEntry>>(l => l.Count == 2)))
            .Verifiable();

        var sut = new EventStreamHashService(uow.Object);

        // Act
        await sut.HashEventsAsync(CancellationToken.None);

        // Assert
        repo.Verify(r => r.UpdateRange(It.IsAny<IReadOnlyList<EventStreamEntry>>()), Times.Once);
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(entriesBatch1, e =>
        {
            Assert.NotNull(e.PreviousHash);
            Assert.NotNull(e.Hash);
        });
    }

    [Fact]
    public async Task A_batch_whose_predecessor_is_not_chained_yet_is_left_for_the_next_pass()
    {
        var repo = new Mock<IEventStreamRepository>();
        var transaction = new Mock<ITransaction>();
        var uow = new Mock<IWriteUnitOfWork>();
        uow.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(transaction.Object);
        uow.Setup(u => u.CreateEventStreamRepository(transaction.Object)).Returns(repo.Object);

        var entry = Entry(sequenceNumber: 3);
        repo.SetupSequence(r => r.GetUnhashedEventsAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry])
            .ReturnsAsync([]);
        repo.Setup(r => r.GetPreviousEventAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(Entry(sequenceNumber: 2));

        await new EventStreamHashService(uow.Object).HashEventsAsync(TestContext.Current.CancellationToken);

        Assert.Null(entry.Hash);
        Assert.Null(entry.PreviousHash);
        repo.Verify(r => r.UpdateRange(It.IsAny<IReadOnlyList<EventStreamEntry>>()), Times.Never);
        transaction.Verify(t => t.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_first_entry_of_the_store_chains_off_the_genesis_seed()
    {
        var repo = new Mock<IEventStreamRepository>();
        var transaction = new Mock<ITransaction>();
        var uow = new Mock<IWriteUnitOfWork>();
        uow.Setup(u => u.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(transaction.Object);
        uow.Setup(u => u.CreateEventStreamRepository(transaction.Object)).Returns(repo.Object);

        var entry = Entry(sequenceNumber: 1);
        repo.SetupSequence(r => r.GetUnhashedEventsAsync(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([entry])
            .ReturnsAsync([]);
        repo.Setup(r => r.GetPreviousEventAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((EventStreamEntry?)null);

        await new EventStreamHashService(uow.Object).HashEventsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SHA256.HashData("GENESIS"u8), entry.PreviousHash);
        Assert.NotNull(entry.Hash);
        repo.Verify(r => r.UpdateRange(It.IsAny<IReadOnlyList<EventStreamEntry>>()), Times.Once);
    }

    private static EventStreamEntry Entry(long sequenceNumber) => new()
    {
        Id = Guid.NewGuid(),
        StreamId = Guid.NewGuid(),
        SequenceNumber = sequenceNumber,
        Version = 1,
        EventTypeName = "TestCreated",
        AggregateTypeName = "TestAggregate",
        DataJson = "{}",
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10),
        BucketId = 0,
        TenantId = Guid.Empty,
        ActorTenantId = Guid.Empty,
        ActorUserId = Guid.Empty
    };
}
