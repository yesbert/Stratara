using Stratara.EventSourcing.EntityFrameworkCore.ValueGenerators;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests.ValueGenerators;

public class GuidV7ValueGeneratorTests
{
    [Fact]
    public void GeneratesTemporaryValues_ReturnsFalse()
    {
        var generator = new GuidV7ValueGenerator();

        Assert.False(generator.GeneratesTemporaryValues);
    }

    [Fact]
    public void Next_ReturnsNonEmptyGuid()
    {
        var generator = new GuidV7ValueGenerator();

        var result = generator.Next(null!);

        Assert.NotEqual(Guid.Empty, result);
    }

    [Fact]
    public void Next_ReturnsUniqueValues()
    {
        var generator = new GuidV7ValueGenerator();

        var guid1 = generator.Next(null!);
        var guid2 = generator.Next(null!);

        Assert.NotEqual(guid1, guid2);
    }

    [Fact]
    public void Next_ReturnsVersion7Guid()
    {
        var generator = new GuidV7ValueGenerator();

        var result = generator.Next(null!);
        var bytes = result.ToByteArray();

        var version = (bytes[7] >> 4) & 0xF;
        Assert.Equal(7, version);
    }
}
