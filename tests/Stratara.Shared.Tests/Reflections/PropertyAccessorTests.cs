using Stratara.Shared.Reflections;

namespace Stratara.Shared.Tests.Reflections;

public class PropertyAccessorTests
{
    private sealed class TestEntity
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public string? NullableValue { get; set; }
    }

    private sealed class ReadOnlyEntity
    {
        public string ReadOnly { get; } = "Immutable";
        public string Writable { get; set; } = "";
    }

    [Fact]
    public void GetOrCreateGetter_ReturnsCorrectValue()
    {
        var entity = new TestEntity { Name = "Alice", Age = 30 };

        var getter = PropertyAccessorCache.GetOrCreateGetter<TestEntity, string>("Name");

        Assert.Equal("Alice", getter(entity));
    }

    [Fact]
    public void GetOrCreateSetter_SetsValue()
    {
        var entity = new TestEntity { Name = "Alice" };

        var setter = PropertyAccessorCache.GetOrCreateSetter<TestEntity, string>("Name");
        setter(entity, "Bob");

        Assert.Equal("Bob", entity.Name);
    }

    [Fact]
    public void GetOrCreateGetter_CachesDelegate()
    {
        var getter1 = PropertyAccessorCache.GetOrCreateGetter<TestEntity, string>("Name");
        var getter2 = PropertyAccessorCache.GetOrCreateGetter<TestEntity, string>("Name");

        Assert.Same(getter1, getter2);
    }

    [Fact]
    public void GetOrCreateSetter_CachesDelegate()
    {
        var setter1 = PropertyAccessorCache.GetOrCreateSetter<TestEntity, int>("Age");
        var setter2 = PropertyAccessorCache.GetOrCreateSetter<TestEntity, int>("Age");

        Assert.Same(setter1, setter2);
    }

    [Fact]
    public void SetValueByName_TypedOverload_SetsProperty()
    {
        var entity = new TestEntity();

        PropertyAccessorCache.SetValueByName<TestEntity, string>(entity, "Name", "Charlie");

        Assert.Equal("Charlie", entity.Name);
    }

    [Fact]
    public void SetValueByName_UntypedOverload_SetsProperty()
    {
        var entity = new TestEntity();

        PropertyAccessorCache.SetValueByName(entity, "Name", (object)"Dave");

        Assert.Equal("Dave", entity.Name);
    }

    [Fact]
    public void GetValueByName_ReturnsPropertyValue()
    {
        var entity = new TestEntity { Age = 42 };

        var result = PropertyAccessorCache.GetValueByName<TestEntity, int>(entity, "Age");

        Assert.Equal(42, result);
    }

    [Fact]
    public void SetValueByName_NullValue_SetsNull()
    {
        var entity = new TestEntity { NullableValue = "HasValue" };

        PropertyAccessorCache.SetValueByName<TestEntity, string?>(entity, "NullableValue", null);

        Assert.Null(entity.NullableValue);
    }

    [Fact]
    public void GetOrCreateGetter_ReadOnlyProperty_Works()
    {
        var entity = new ReadOnlyEntity();

        var getter = PropertyAccessorCache.GetOrCreateGetter<ReadOnlyEntity, string>("ReadOnly");
        var result = getter(entity);

        Assert.Equal("Immutable", result);
    }

    [Fact]
    public void SetValueByName_NonExistentProperty_ThrowsException()
    {
        var entity = new TestEntity();

        Assert.Throws<ArgumentException>(() =>
            PropertyAccessorCache.SetValueByName<TestEntity, string>(entity, "DoesNotExist", "value"));
    }

    [Fact]
    public void PropertyAccessor_ReadOnlyProperty_HasNoSetter()
    {
        var prop = typeof(ReadOnlyEntity).GetProperty("ReadOnly")!;
        var accessor = new PropertyAccessor(prop, typeof(ReadOnlyEntity));

        Assert.Equal("ReadOnly", accessor.Name);
        Assert.Equal(typeof(string), accessor.PropertyType);

        var entity = new ReadOnlyEntity();
        Assert.Equal("Immutable", accessor.GetValue(entity));

        accessor.SetValue(entity, "Changed");
        Assert.Equal("Immutable", accessor.GetValue(entity));
    }

    [Fact]
    public void PropertyAccessor_WritableProperty_GetAndSet()
    {
        var prop = typeof(TestEntity).GetProperty("Name")!;
        var accessor = new PropertyAccessor(prop, typeof(TestEntity));

        Assert.Equal("Name", accessor.Name);
        Assert.Equal(typeof(string), accessor.PropertyType);

        var entity = new TestEntity { Name = "Original" };
        Assert.Equal("Original", accessor.GetValue(entity));

        accessor.SetValue(entity, "Updated");
        Assert.Equal("Updated", accessor.GetValue(entity));
    }

    [Fact]
    public void PropertyAccessor_IntProperty_GetAndSet()
    {
        var prop = typeof(TestEntity).GetProperty("Age")!;
        var accessor = new PropertyAccessor(prop, typeof(TestEntity));

        var entity = new TestEntity { Age = 25 };
        Assert.Equal(25, accessor.GetValue(entity));

        accessor.SetValue(entity, 30);
        Assert.Equal(30, accessor.GetValue(entity));
    }
}
