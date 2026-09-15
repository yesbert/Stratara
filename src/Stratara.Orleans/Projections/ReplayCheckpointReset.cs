using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The full replay's truncation, extended for store-reading projections: before the host's truncator
/// empties the read models, every projection grain is paused and its checkpoints are returned to the
/// beginning; the grains are resumed afterwards, however the truncation ended. A replay keeps them
/// suspended until it ends, and they then read the store from the beginning — so no checkpoint is left
/// past an entry whose effect the replay removed, and a truncation that fails part-way is repaired by
/// re-reading.
/// </summary>
internal sealed class ReplayCheckpointResetTruncator(
    IProjectionViewTruncator inner,
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder) : IProjectionViewTruncator
{
    private readonly int _partitionCount = commitOrder.Value.PartitionCount;

    public async Task TruncateAllAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var handler = services.GetRequiredService<IProjectionHandler>();
        var names = services.GetServices<IProjection>().Select(handler.GetProjectionName).Distinct(StringComparer.Ordinal).ToList();
        var grains = names
            .SelectMany(name => Enumerable.Range(0, _partitionCount).Select(partition => grainFactory.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(name, partition))))
            .ToList();

        await Task.WhenAll(grains.Select(grain => grain.PauseAsync()));
        try
        {
            var checkpoints = services.GetRequiredService<IProjectionCheckpointStore>();
            var reader = services.GetRequiredService<ICommittedPositionReader>().Name;
            await Task.WhenAll(names.SelectMany(name => Enumerable.Range(0, _partitionCount)
                .Select(partition => checkpoints.SetAsync(name, partition, reader, 0, cancellationToken))));

            await inner.TruncateAllAsync(cancellationToken);
        }
        finally
        {
            await Task.WhenAll(grains.Select(grain => grain.ResumeAsync()));
        }
    }

    /// <summary>
    /// Wraps the truncator registered before the store-reading projections. A truncator registered after
    /// them is not wrapped; <see cref="ReplayTruncatorOrderCheck"/> refuses to start such a host.
    /// </summary>
    public static void Decorate(IServiceCollection services, Func<IServiceProvider, ServiceDescriptor, object> instantiate)
    {
        if (services.Any(d => d.ServiceType == typeof(ReplayCheckpointResetMarker)))
        {
            return;
        }

        services.AddSingleton(ReplayCheckpointResetMarker.Instance);
        services.TryAddEnumerableHostedCheck();
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IProjectionViewTruncator));
        if (existing is null)
        {
            return;
        }

        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(
            typeof(IProjectionViewTruncator),
            sp => ActivatorUtilities.CreateInstance<ReplayCheckpointResetTruncator>(sp, (IProjectionViewTruncator)instantiate(sp, existing)),
            existing.Lifetime));
    }
}

/// <summary>Marks a service collection whose truncator the store-reading projections already wrapped.</summary>
internal sealed class ReplayCheckpointResetMarker
{
    public static readonly ReplayCheckpointResetMarker Instance = new();
}

/// <summary>
/// Refuses to let a host start whose full replay would empty the read models without returning the store
/// readers' checkpoints to the beginning — a truncator registered after the store-reading projections.
/// </summary>
internal sealed class ReplayTruncatorOrderCheck(IServiceScopeFactory scopeFactory) : IHostedService
{
    /// <exception cref="InvalidOperationException">The registered truncator is not wrapped.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var truncator = scope.ServiceProvider.GetService<IProjectionViewTruncator>();
        if (truncator is null or ReplayCheckpointResetTruncator)
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            $"The {nameof(IProjectionViewTruncator)} {truncator.GetType().Name} was registered after AddStrataraProjectionGrains, so a full replay would empty the read models without returning the store readers' checkpoints to the beginning. Register the truncator before AddStrataraProjectionGrains.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class ReplayTruncatorOrderCheckRegistration
{
    public static void TryAddEnumerableHostedCheck(this IServiceCollection services) =>
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(
            services,
            ServiceDescriptor.Singleton<IHostedService, ReplayTruncatorOrderCheck>());
}
