using System.Diagnostics.CodeAnalysis;
using Stratara.Shared.Reflections;

namespace Stratara.Shared.Tests.Reflections;

[SuppressMessage(
    "Usage",
    "CA2263:Prefer generic overload when type is known",
    Justification = "Tests intentionally exercise the non-generic by-Type overload alongside the generic one.")]
public class ObjectFactoryTests
{
    private sealed class SimpleClass
    {
        public string Name { get; set; } = "Default";
    }

    private sealed class AnotherClass
    {
        public int Value { get; set; }
    }

    [Fact]
    public void CreateInstance_ByType_ReturnsInstance()
    {
        var result = ObjectFactory.CreateInstance(typeof(SimpleClass));

        Assert.NotNull(result);
        Assert.IsType<SimpleClass>(result);
    }

    [Fact]
    public void CreateInstance_Generic_ReturnsTypedInstance()
    {
        var result = ObjectFactory.CreateInstance<SimpleClass>();

        Assert.NotNull(result);
        Assert.Equal("Default", result.Name);
    }

    [Fact]
    public void CreateInstance_CachesFactory()
    {
        var result1 = ObjectFactory.CreateInstance(typeof(SimpleClass));
        var result2 = ObjectFactory.CreateInstance(typeof(SimpleClass));

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotSame(result1, result2);
    }

    [Fact]
    public void CreateInstance_ClassWithDefaultConstructor_Works()
    {
        var result = ObjectFactory.CreateInstance<AnotherClass>();

        Assert.NotNull(result);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void CreateInstance_DifferentTypes_CreatesDifferentInstances()
    {
        var simple = ObjectFactory.CreateInstance(typeof(SimpleClass));
        var another = ObjectFactory.CreateInstance(typeof(AnotherClass));

        Assert.IsType<SimpleClass>(simple);
        Assert.IsType<AnotherClass>(another);
    }

    [Fact]
    public void CreateInstance_NullType_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ObjectFactory.CreateInstance(null!));
    }
}
