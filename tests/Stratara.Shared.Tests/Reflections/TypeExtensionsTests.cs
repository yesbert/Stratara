using Stratara.Shared.Reflections;

namespace Stratara.Shared.Tests.Reflections;

public class TypeExtensionsTests
{
    public sealed class SampleType;

    [Fact]
    public void GetQualifiedTypeName_ReturnsAssemblyQualifiedName()
    {
        var typeName = typeof(SampleType).GetQualifiedTypeName();

        Assert.Contains(nameof(SampleType), typeName);
        Assert.Equal(typeof(SampleType).AssemblyQualifiedName, typeName);
    }

    [Fact]
    public void GetQualifiedTypeName_IsCached_ReturnsIdenticalReferenceOnSecondCall()
    {
        var first = typeof(SampleType).GetQualifiedTypeName();
        var second = typeof(SampleType).GetQualifiedTypeName();

        Assert.Same(first, second);
    }

    [Fact]
    public void GetVersionIndependentTypeName_StripsVersionCultureAndPublicKey()
    {
        const string fullyQualified =
            "Stratara.Sample.Foo, Stratara.Sample, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null";

        var result = fullyQualified.GetVersionIndependentTypeName();

        Assert.Equal("Stratara.Sample.Foo, Stratara.Sample", result);
    }

    [Fact]
    public void GetVersionIndependentTypeName_NoCommas_ReturnsInputUnchanged()
    {
        const string unqualified = "Foo";

        var result = unqualified.GetVersionIndependentTypeName();

        Assert.Equal(unqualified, result);
    }

    [Fact]
    public void GetVersionIndependentTypeName_SingleComma_ReturnsInputUnchanged()
    {
        const string typeAndAssembly = "Stratara.Sample.Foo, Stratara.Sample";

        var result = typeAndAssembly.GetVersionIndependentTypeName();

        Assert.Equal(typeAndAssembly, result);
    }

    [Fact]
    public void GetVersionIndependentTypeName_TrimsWhitespace()
    {
        const string padded = "Foo,Bar   , Version=1.0";

        var result = padded.GetVersionIndependentTypeName();

        Assert.Equal("Foo,Bar", result);
    }
}
