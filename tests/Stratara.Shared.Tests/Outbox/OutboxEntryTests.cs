using Stratara.Abstractions.Outbox;

namespace Stratara.Shared.Tests.Outbox;

public class OutboxEntryTests
{
    [Fact]
    public void Properties_SetAndGet()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;

        var sut = new OutboxEntry
        {
            Id = id,
            DataJson = "{\"command\":\"test\"}",
            DataTypeName = "TestCommand",
            Timestamp = timestamp,
            BucketId = 3,
            RowVersion = 5
        };

        Assert.Equal(id, sut.Id);
        Assert.Equal("{\"command\":\"test\"}", sut.DataJson);
        Assert.Equal("TestCommand", sut.DataTypeName);
        Assert.Equal(timestamp, sut.Timestamp);
        Assert.Equal(3, sut.BucketId);
        Assert.Equal(5u, sut.RowVersion);
    }

    [Fact]
    public void DefaultValues_AreInitialized()
    {
        var sut = new OutboxEntry
        {
            DataJson = "{}",
            DataTypeName = "T",
            BucketId = 0
        };

        Assert.Equal(Guid.Empty, sut.Id);
        Assert.Equal(0u, sut.RowVersion);
        Assert.Equal(default, sut.Timestamp);
    }
}
