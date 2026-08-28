using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Resilience;
using Stratara.Resilience.Mediator;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extension that hooks Stratara's named Polly resilience pipelines into the in-process mediator
/// pipeline via an opt-in, per-request behavior.
/// </summary>
public static class ResilienceBehaviorServiceCollectionExtensions
{
    /// <summary>
    /// Register the resilience pipeline behavior so any mediator request implementing
    /// <see cref="IResilientRequest"/> is dispatched inside the named Polly pipeline it selects.
    /// Requests that do not implement the marker are unaffected.
    /// </summary>
    /// <remarks>
    /// Requires the named pipelines to be registered (<see cref="ResilienceServiceCollectionExtensions.AddResiliencePipelines"/>
    /// or a consumer's own <c>AddResiliencePipeline(name, ...)</c>) and the mediator to be added.
    /// Call this <b>after</b> <c>AddStrataraValidation()</c> / <c>AddStrataraTenantIsolation()</c> so
    /// the retry wraps the handler and not the validation / authorization guards (which should not be
    /// re-run on retry). Behaviors are open generics resolved per request. Calling this more than
    /// once installs the behavior once, so a request is not retried twice over.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Acts only on requests implementing <c>IResilientRequest</c>; everything else passes through:
    /// <code>
    /// services.AddResiliencePipelines();
    /// services.AddStrataraResilienceBehavior();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraResilienceBehavior(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(ResiliencePipelineBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<>), typeof(ResiliencePipelineBehavior<>)));
        return services;
    }
}
