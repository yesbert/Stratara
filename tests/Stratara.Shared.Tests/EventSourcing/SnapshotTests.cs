using Stratara.Shared.EventSourcing;
using Stratara.Abstractions.EventSourcing;

namespace Stratara.Shared.Tests.EventSourcing;

public class SnapshotTests
{
    [Fact]
    public void Properties_SetAndGet()
    {
        var id = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;

        var sut = new Snapshot
        {
            Id = id,
            StreamId = streamId,
            Version = 42,
            AggregateTypeName = "MyAggregate",
            DataJson = "{\"key\":\"value\"}",
            Timestamp = timestamp,
            BucketId = 7,
            RowVersion = 3,
            TenantId = tenantId
        };

        Assert.Equal(id, sut.Id);
        Assert.Equal(streamId, sut.StreamId);
        Assert.Equal(42, sut.Version);
        Assert.Equal("MyAggregate", sut.AggregateTypeName);
        Assert.Equal("{\"key\":\"value\"}", sut.DataJson);
        Assert.Equal(timestamp, sut.Timestamp);
        Assert.Equal(7, sut.BucketId);
        Assert.Equal(3u, sut.RowVersion);
        Assert.Equal(tenantId, sut.TenantId);
    }
}
