using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.Reflections;

namespace Stratara.Shared.Tests.Reflections;

public class TrustedTypeResolverServiceCollectionExtensionsTests
{
    public sealed class FakeAggregateOne : IAggregate
    {
        public Guid Id { get; set; }
    }

    public sealed class FakeAggregateTwo : IAggregate
    {
        public Guid Id { get; set; }
    }

    public abstract class AbstractAggregate : IAggregate
    {
        public Guid Id { get; set; }
    }

    public sealed record FakeAggregateCreated(Guid Id);

    public sealed record FakeAggregateRenamed(string Name);

    public sealed record UnrelatedEvent(Guid Id);

    public sealed class FakeAggregateWithApplyMethods : IAggregate
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public void Apply(FakeAggregateCreated @event) => Id = @event.Id;

        public void Apply(FakeAggregateRenamed @event) => Name = @event.Name;

        public void Apply(FakeAggregateCreated first, FakeAggregateRenamed second)
        {
            Id = first.Id;
            Name = second.Name;
        }

        public void Apply() { }
    }

    public interface IUnrelated;

    public sealed class UnrelatedType;

    [Fact]
    public void AddTrustedTypeResolver_RegistersSingletonResolver()
    {
        var services = new ServiceCollection();

        services.AddTrustedTypeResolver();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ITrustedTypeResolver));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.IsType<TrustedTypeResolver>(descriptor.ImplementationInstance);
    }

    [Fact]
    public void AddTrustedTypeResolver_TwiceReusesExistingInstance()
    {
        var services = new ServiceCollection();

        services.AddTrustedTypeResolver();
        var first = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);
        services.AddTrustedTypeResolver();
        var second = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);

        Assert.Same(first, second);
        Assert.Single(services, d => d.ServiceType == typeof(ITrustedTypeResolver));
    }

    [Fact]
    public void AddTrustedType_RegistersTypeInResolver()
    {
        var services = new ServiceCollection();

        services.AddTrustedType<UnrelatedType>();
        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);

        Assert.Contains(resolver.RegisteredTypes, t => t == typeof(UnrelatedType));
    }

    [Fact]
    public void AddTrustedType_ReturnsSameCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddTrustedType<UnrelatedType>();

        Assert.Same(services, result);
    }

    [Fact]
    public void AddAggregatesFromAssemblyContaining_RegistersConcreteAggregates()
    {
        var services = new ServiceCollection();

        services.AddAggregatesFromAssemblyContaining<FakeAggregateOne>();
        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);

        Assert.Contains(resolver.RegisteredTypes, t => t == typeof(FakeAggregateOne));
        Assert.Contains(resolver.RegisteredTypes, t => t == typeof(FakeAggregateTwo));
    }

    [Fact]
    public void AddAggregatesFromAssemblyContaining_SkipsAbstractAndUnrelated()
    {
        var services = new ServiceCollection();

        services.AddAggregatesFromAssemblyContaining<FakeAggregateOne>();
        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);

        Assert.DoesNotContain(resolver.RegisteredTypes, t => t == typeof(AbstractAggregate));
        Assert.DoesNotContain(resolver.RegisteredTypes, t => t == typeof(IUnrelated));
        Assert.DoesNotContain(resolver.RegisteredTypes, t => t == typeof(UnrelatedType));
    }

    [Fact]
    public void AddAggregatesFromAssemblyContaining_RegistersApplyMethodEventParameters()
    {
        var services = new ServiceCollection();

        services.AddAggregatesFromAssemblyContaining<FakeAggregateWithApplyMethods>();
        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);

        Assert.Contains(resolver.RegisteredTypes, t => t == typeof(FakeAggregateWithApplyMethods));
        Assert.Contains(resolver.RegisteredTypes, t => t == typeof(FakeAggregateCreated));
        Assert.Contains(resolver.RegisteredTypes, t => t == typeof(FakeAggregateRenamed));
        Assert.DoesNotContain(resolver.RegisteredTypes, t => t == typeof(UnrelatedEvent));
    }

    [Fact]
    public void AddAggregatesFromAssemblyContaining_IgnoresApplyOverloadsThatAreNotSingleParameter()
    {
        var services = new ServiceCollection();

        services.AddAggregatesFromAssemblyContaining<FakeAggregateWithApplyMethods>();
        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);

        Assert.True(resolver.TryResolve(typeof(FakeAggregateCreated).AssemblyQualifiedName!, out _));
        Assert.True(resolver.TryResolve(typeof(FakeAggregateRenamed).AssemblyQualifiedName!, out _));
    }

    [Fact]
    public void GetOrAddResolver_CreatesNewWhenNothingRegistered()
    {
        var services = new ServiceCollection();

        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);

        Assert.NotNull(resolver);
        Assert.Single(services, d => d.ServiceType == typeof(ITrustedTypeResolver));
    }
}
