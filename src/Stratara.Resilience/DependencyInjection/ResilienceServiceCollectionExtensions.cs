using Microsoft.Extensions.DependencyInjection;
using Polly;
using Stratara.Resilience;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions for Stratara's named Polly resilience pipelines.
/// </summary>
public static class ResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Register every Stratara named resilience pipeline (<see cref="ResilienceNames.MessageBus"/>,
    /// <see cref="ResilienceNames.CommandDispatcher"/>, <see cref="ResilienceNames.EventBundleDispatcher"/>,
    /// <see cref="ResilienceNames.ConcurrencyConflict"/>) with the application's Polly registry.
    /// </summary>
    /// <remarks>
    /// Idempotent — subsequent calls re-register the same pipelines, which Polly tolerates by
    /// keeping the first registration. Resolve a pipeline at the call site via
    /// <c>sp.GetRequiredService&lt;ResiliencePipelineProvider&lt;string&gt;&gt;().GetPipeline(name)</c>.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Registers the named pipelines. Reference them through <c>ResilienceNames</c> rather than by
    /// string literal:
    /// <code>
    /// services.AddResiliencePipelines();
    /// </code>
    /// </example>
    public static IServiceCollection AddResiliencePipelines(this IServiceCollection services)
    {
        services.AddMessageBusResilience();
        services.AddCommandDispatcherPipeline();
        services.AddEventBundleDispatcherPipeline();
        services.AddConcurrencyConflictPipeline();
        services.AddPrecedingFactPipeline();
        return services;
    }

    private static void AddMessageBusResilience(this IServiceCollection services)
    {
        services.AddResiliencePipeline(ResilienceNames.MessageBus, ResilienceFactory.CreateMessageBusPipeline);
    }

    private static void AddCommandDispatcherPipeline(this IServiceCollection services)
    {
        services.AddResiliencePipeline(ResilienceNames.CommandDispatcher, ResilienceFactory.CreateCommandDispatcherPipeline);
    }

    private static void AddEventBundleDispatcherPipeline(this IServiceCollection services)
    {
        services.AddResiliencePipeline(ResilienceNames.EventBundleDispatcher, ResilienceFactory.CreateEventBundleDispatcherPipeline);
    }

    private static void AddConcurrencyConflictPipeline(this IServiceCollection services)
    {
        services.AddResiliencePipeline(ResilienceNames.ConcurrencyConflict, ResilienceFactory.CreateConcurrencyConflictPipeline);
    }

    private static void AddPrecedingFactPipeline(this IServiceCollection services)
    {
        services.AddResiliencePipeline(ResilienceNames.PrecedingFact, ResilienceFactory.CreatePrecedingFactPipeline);
    }
}
