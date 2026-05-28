using Microsoft.Extensions.DependencyInjection;
using Stratara.Infrastructure.BackgroundTasks;
using Stratara.Abstractions.BackgroundTasks;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions that wire the in-process background-task queue and its drain worker.</summary>
public static class BackgroundTaskExtensions
{
    /// <summary>
    /// Registers <see cref="QueuedHostedService"/> and a singleton <see cref="IBackgroundTaskQueue"/>
    /// with a default capacity of 100 pending items.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddBackgroundTasks(this IServiceCollection services)
    {
        services.AddHostedService<QueuedHostedService>();
        services.AddSingleton<IBackgroundTaskQueue>(_ => new BackgroundTaskQueue(100));
        return services;
    }
}
