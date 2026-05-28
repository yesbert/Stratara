using Stratara.Shared.Partitioning;

namespace Stratara.Shared.Tests;

public class BucketCalculatorTests
{
    [Fact]
    public void GetBucketId_ReturnsValueWithinRange()
    {
        // Arrange
        var id = Guid.NewGuid();

        // Act
        var bucket = BucketCalculator.GetBucketId(id);

        // Assert
        Assert.InRange(bucket, 0, BucketConstants.TotalBucketCount - 1);
    }

    [Fact]
    public void GetBucketId_IsStableForSameGuid()
    {
        // Arrange
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

        // Act
        var first = BucketCalculator.GetBucketId(id);
        var second = BucketCalculator.GetBucketId(id);

        // Assert
        Assert.Equal(first, second);
    }
}
