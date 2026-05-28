using Stratara.Shared.EventSourcing;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Shared.Tests.EventSourcing;

public class EventStreamEntryTests
{
    [Fact]
    public void Properties_SetAndGet()
    {
        var id = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var hash = new byte[] { 1, 2, 3 };
        var prevHash = new byte[] { 4, 5, 6 };

        var sut = new EventStreamEntry
        {
            Id = id,
            SequenceNumber = 100,
            StreamId = streamId,
            Version = 5,
            EventTypeName = "TestEvent",
            AggregateTypeName = "TestAggregate",
            DataJson = "{\"data\":true}",
            Timestamp = timestamp,
            CorrelationId = "corr-123",
            CausationId = "cause-456",
            PreviousHash = prevHash,
            Hash = hash,
            BucketId = 3,
            RowVersion = 7,
            TenantId = tenantId,
            ActorTenantId = tenantId,
            ActorUserId = userId
        };

        Assert.Equal(id, sut.Id);
        Assert.Equal(100, sut.SequenceNumber);
        Assert.Equal(streamId, sut.StreamId);
        Assert.Equal(5, sut.Version);
        Assert.Equal("TestEvent", sut.EventTypeName);
        Assert.Equal("TestAggregate", sut.AggregateTypeName);
        Assert.Equal("{\"data\":true}", sut.DataJson);
        Assert.Equal(timestamp, sut.Timestamp);
        Assert.Equal("corr-123", sut.CorrelationId);
        Assert.Equal("cause-456", sut.CausationId);
        Assert.Equal(prevHash, sut.PreviousHash);
        Assert.Equal(hash, sut.Hash);
        Assert.Equal(3, sut.BucketId);
        Assert.Equal(7u, sut.RowVersion);
        Assert.Equal(tenantId, sut.TenantId);
        Assert.Equal(userId, sut.ActorUserId);
    }

    [Fact]
    public void NullableProperties_DefaultToNull()
    {
        var sut = new EventStreamEntry
        {
            StreamId = Guid.NewGuid(),
            Version = 1,
            EventTypeName = "E",
            AggregateTypeName = "A",
            DataJson = "{}",
            BucketId = 0,
            TenantId = Guid.NewGuid(),
            ActorTenantId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid()
        };

        Assert.Null(sut.CorrelationId);
        Assert.Null(sut.CausationId);
        Assert.Null(sut.PreviousHash);
        Assert.Null(sut.Hash);
    }
}
