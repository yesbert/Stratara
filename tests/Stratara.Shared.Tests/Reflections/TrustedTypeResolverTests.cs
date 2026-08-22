using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Stratara.Abstractions.Reflections;

namespace Stratara.Shared.Tests.Reflections;

public class TrustedTypeResolverTests
{
    public sealed record GenericEvent<TPayload>(TPayload Payload);

    public sealed record PayloadA(string Value);

    public sealed record PayloadB(int Value);

    private static string WithUpgradedAssemblyVersions(string assemblyQualifiedName) =>
        Regex.Replace(assemblyQualifiedName, @"Version=[\d.]+", "Version=99.0.0.0");

    [Fact]
    public void Register_ClosedGeneric_ResolvesByItsAssemblyQualifiedName()
    {
        var resolver = new TrustedTypeResolver();
        var type = typeof(GenericEvent<PayloadA>);

        resolver.Register(type);

        Assert.Same(type, resolver.Resolve(type.AssemblyQualifiedName!));
    }

    [Fact]
    public void Register_ClosedGeneric_ResolvesAfterTheProducingAssembliesAreUpgraded()
    {
        var resolver = new TrustedTypeResolver();
        var type = typeof(GenericEvent<PayloadA>);
        resolver.Register(type);

        var persisted = WithUpgradedAssemblyVersions(type.AssemblyQualifiedName!);

        Assert.Same(type, resolver.Resolve(persisted));
    }

    [Fact]
    public void Register_ClosedGenerics_DifferingOnlyInTypeArgument_DoNotCollide()
    {
        var resolver = new TrustedTypeResolver();
        resolver.Register(typeof(GenericEvent<PayloadA>));
        resolver.Register(typeof(GenericEvent<PayloadB>));

        Assert.Same(typeof(GenericEvent<PayloadA>), resolver.Resolve(typeof(GenericEvent<PayloadA>).AssemblyQualifiedName!));
        Assert.Same(typeof(GenericEvent<PayloadB>), resolver.Resolve(typeof(GenericEvent<PayloadB>).AssemblyQualifiedName!));
    }

    [Fact]
    public void Register_SameTypeTwice_IsANoOp()
    {
        var resolver = new TrustedTypeResolver();

        resolver.Register(typeof(GenericEvent<PayloadA>));
        resolver.Register(typeof(GenericEvent<PayloadA>));

        Assert.Single(resolver.RegisteredTypes);
    }

    private static Type DefineTypeIn(string assemblyName, string typeName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        return assembly.DefineDynamicModule(assemblyName).DefineType(typeName, TypeAttributes.Public).CreateType();
    }

    [Fact]
    public void Register_DifferentTypeUnderAnAlreadyRegisteredName_Throws()
    {
        var first = DefineTypeIn("Contoso.Events", "Contoso.OrderPlaced");
        var second = DefineTypeIn("Contoso.Events", "Contoso.OrderPlaced");
        var resolver = new TrustedTypeResolver();
        resolver.Register(first);

        var exception = Assert.Throws<InvalidOperationException>(() => resolver.Register(second));

        Assert.Contains("Contoso.OrderPlaced", exception.Message, StringComparison.Ordinal);
        Assert.Same(first, resolver.Resolve(first.AssemblyQualifiedName!));
    }
}
