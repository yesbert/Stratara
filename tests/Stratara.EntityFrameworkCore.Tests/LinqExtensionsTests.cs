using Stratara.EventSourcing.EntityFrameworkCore.Extensions;

namespace Stratara.EventSourcing.EntityFrameworkCore.Tests;

public class LinqExtensionsTests
{
    [Fact]
    public void OrderBy_ByPropertyName_SortsAscending()
    {
        var data = new List<Item>
        {
            new(2, "b", 30),
            new(1, "c", 10),
            new(3, "a", 20)
        };

        var ordered = data.AsQueryable().OrderBy("Name").ToList();

        Assert.Equal(["a", "b", "c"], ordered.Select(i => i.Name));
    }

    [Fact]
    public void OrderByDescending_ByPropertyName_SortsDescending()
    {
        var data = new List<Item>
        {
            new(2, "b", 30),
            new(1, "c", 10),
            new(3, "a", 20)
        };

        var ordered = data.AsQueryable().OrderByDescending("Value").ToList();

        Assert.Equal([30, 20, 10], ordered.Select(i => i.Value));
    }

    [Fact]
    public void OrderBy_InvalidProperty_Throws()
    {
        var data = new List<Item> { new(1, "x", 1) };
        Assert.Throws<ArgumentException>(() => data.AsQueryable().OrderBy("DoesNotExist").ToList());
    }

    // ReSharper disable once NotAccessedPositionalProperty.Local
    private record Item(int Id, string Name, int Value);
}