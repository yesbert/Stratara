using Stratara.Shared.Reflections;

namespace Stratara.Shared.Tests;

public class ReflectionsTests
{
    private class Sample
    {
        public int Number { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void TypeExtensions_GetQualifiedTypeName_ReturnsStableValue()
    {
        // Arrange
        var type = typeof(Sample);

        // Act
        var first = type.GetQualifiedTypeName();
        var second = type.GetQualifiedTypeName();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
        Assert.Contains(typeof(Sample).FullName!, first);
    }

    [Fact]
    public void PropertyAccessorCache_GetSet_Works_For_Public_Instance_Properties()
    {
        // Arrange
        var s = new Sample { Number = 1, Name = "Old" };

        // Act
        var number = PropertyAccessorCache.GetValueByName<Sample, int>(s, nameof(Sample.Number));
        PropertyAccessorCache.SetValueByName(s, nameof(Sample.Name), "New");

        // Assert
        Assert.Equal(1, number);
        Assert.Equal("New", s.Name);
    }

    [Fact]
    public void PropertyAccessor_Invokes_Getter_And_Setter()
    {
        // Arrange
        var propInfo = typeof(Sample).GetProperty(nameof(Sample.Name))!;
        var accessor = new PropertyAccessor(propInfo, typeof(Sample));
        var s = new Sample { Name = "A" };

        // Act
        var before = accessor.GetValue(s);
        accessor.SetValue(s, "B");
        var after = accessor.GetValue(s);

        // Assert
        Assert.Equal("A", before);
        Assert.Equal("B", after);
        Assert.Equal("B", s.Name);
    }

    [Fact]
    public void PropertyAccessorCache_Throws_On_Unknown_Property()
    {
        // Arrange
        var s = new Sample();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => PropertyAccessorCache.SetValueByName(s, "DoesNotExist", 10));
    }
}
