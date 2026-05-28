using Stratara.Abstractions.EventSourcing;

namespace Stratara.Shared.Tests.EventSourcing;

public class EventChainAnchorTests
{
    [Fact]
    public void Properties_SetAndGet()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var anchorHash = new byte[] { 10, 20, 30, 40 };

        var sut = new EventChainAnchor
        {
            Id = id,
            TenantId = tenantId,
            SequenceNumber = 999,
            AnchorHash = anchorHash,
            Timestamp = timestamp,
            BlockchainTxHash = "0xabc123",
            BucketId = 5,
            RowVersion = 2
        };

        Assert.Equal(id, sut.Id);
        Assert.Equal(tenantId, sut.TenantId);
        Assert.Equal(999, sut.SequenceNumber);
        Assert.Equal(anchorHash, sut.AnchorHash);
        Assert.Equal(timestamp, sut.Timestamp);
        Assert.Equal("0xabc123", sut.BlockchainTxHash);
        Assert.Equal(5, sut.BucketId);
        Assert.Equal(2u, sut.RowVersion);
    }

    [Fact]
    public void AnchorHash_DefaultsToEmptyArray()
    {
        var sut = new EventChainAnchor
        {
            BucketId = 0,
            TenantId = Guid.NewGuid()
        };

        Assert.NotNull(sut.AnchorHash);
        Assert.Empty(sut.AnchorHash);
    }

    [Fact]
    public void BlockchainTxHash_Nullable()
    {
        var sut = new EventChainAnchor
        {
            BucketId = 0,
            TenantId = Guid.NewGuid()
        };

        Assert.Null(sut.BlockchainTxHash);
    }

    [Fact]
    public void SequenceNumber_CanBeSetToLargeValue()
    {
        var sut = new EventChainAnchor
        {
            BucketId = 0,
            TenantId = Guid.NewGuid(),
            SequenceNumber = long.MaxValue
        };

        Assert.Equal(long.MaxValue, sut.SequenceNumber);
    }
}
