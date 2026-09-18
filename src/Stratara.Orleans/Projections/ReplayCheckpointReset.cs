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
/// beginning — whatever reader name they were written under — and returned there once more after the truncation, so
/// what a reader applied before it although it should have been paused is applied again; the grains are resumed
/// afterwards, however the truncation ended, and a pause that fails leaves none of them paused. A replay keeps them
/// suspended until it ends, and they then read the store from the beginning — so no checkpoint is left
/// past an entry whose effect the replay removed, and a truncation that fails part-way is repaired by
/// re-reading.
/// </summary>
internal sealed class ReplayCheckpointResetTruncator(
    IProjectionViewTruncator inner,
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder,
    StoreReaderLease lease) : IProjectionViewTruncator
{
    private readonly int _partitionCount = commitOrder.Value.PartitionCount;

    public async Task TruncateAllAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var handler = services.GetRequiredService<IProjectionHandler>();
        var names = services.GetServices<IProjection>().Select(handler.GetProjectionName).Distinct(StringComparer.Ordinal).ToList();
        var grains = names
            .SelectMany(name => Enumerable.Range(0, _partitionCount).Select(partition => Reader(StoreReaderGrainKey.Of(name, partition))))
            .ToList();

        await using var hold = await StoreReaderPause.PauseAllAsync(grains, lease);
        var checkpoints = services.GetRequiredService<IProjectionCheckpointStore>();
        var reader = services.GetRequiredService<ICommittedPositionReader>().Name;
        await TruncationBetweenResets.RunAsync(
            token => Task.WhenAll(names.SelectMany(name => Enumerable.Range(0, _partitionCount)
                .Select(partition => checkpoints.ResetAsync(name, partition, reader, token)))),
            inner.TruncateAllAsync,
            hold.QuiesceAsync,
            cancellationToken);

        await hold.ResumeAsync();
    }

    private PausedReader Reader(string key)
    {
        var grain = grainFactory.GetGrain<IProjectionGrain>(key);
        return new PausedReader(key, grain.PauseAsync, grain.RenewPauseAsync, grain.ResumeAsync);
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
