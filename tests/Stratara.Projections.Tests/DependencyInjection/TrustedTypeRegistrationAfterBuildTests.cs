using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Reflections;

namespace Stratara.Projections.Tests.DependencyInjection;

/// <summary>
/// Scenario <em>A trusted type is added after the host was built</em>: the set of types the host trusts is one
/// resolver every registration adds to, and a built host reads it. Once the host was built from the service collection
/// — which makes it read-only — a further registration fails, naming itself, and the running host does not trust the
/// type; registering the resolver again, which would change nothing, still succeeds.
/// </summary>
public sealed class TrustedTypeRegistrationAfterBuildTests
{
    [Fact]
    public void A_trusted_type_added_after_the_host_was_built_is_refused()
    {
        var services = new ServiceCollection();
        services.AddTrustedType<Trusted>();
        services.MakeReadOnly();

        var refusals = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            [nameof(TrustedTypeResolverServiceCollectionExtensions.AddTrustedType)] = () => services.AddTrustedType<Late>(),
            [nameof(TrustedTypeResolverServiceCollectionExtensions.AddAggregatesFromAssemblyContaining)] = () => services.AddAggregatesFromAssemblyContaining<Late>(),
            [nameof(TrustedTypeResolverServiceCollectionExtensions.AddDomainEventTypesFromAssemblyContaining)] = () => services.AddDomainEventTypesFromAssemblyContaining<Late>(),
            [nameof(ProjectionServiceCollectionExtensions.AddProjectionsFromAssemblyContaining)] = () => services.AddProjectionsFromAssemblyContaining<Late>(),
        };

        foreach (var (registration, register) in refusals)
        {
            var refused = Assert.Throws<InvalidOperationException>(register);
            Assert.StartsWith($"{registration} was called", refused.Message, StringComparison.Ordinal);
        }

        services.AddTrustedTypeResolver();
        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<ITrustedTypeResolver>();
        Assert.True(resolver.TryResolve(typeof(Trusted).AssemblyQualifiedName!, out _));
        Assert.False(resolver.TryResolve(typeof(Late).AssemblyQualifiedName!, out _));
    }

    private sealed record Trusted;

    private sealed record Late;
}
