using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.Diagnostics;
using Stratara.Orleans.Sagas;

namespace Stratara.Orleans.Projections;

/// <summary>
/// The full replay's truncation, extended for store-reading projections: before the host's truncator
/// empties the read models, every projection grain is paused and its checkpoints are returned to the
/// beginning — whatever reader name they were written under — and returned there once more after the truncation, so
/// what a reader applied before it although it should have been paused is applied again; the grains are resumed
/// afterwards, however the truncation ended, and a pause that fails leaves none of them paused. A replay keeps them
/// suspended until it ends, and they then read the store from the beginning — so no checkpoint is left
/// past an entry whose effect the replay removed, and a truncation that fails part-way is repaired by
/// re-reading. The saga positions are not the replay's to move: <see cref="ReplaySagaPositions"/> remembers them
/// before anything changes and writes back what the host's truncator took.
/// </summary>
internal sealed class ReplayCheckpointResetTruncator(
    IProjectionViewTruncator inner,
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<CommitOrderOptions> commitOrder,
    ProjectionRegistrations registrations,
    StoreReaderLease lease,
    ILogger<ReplayCheckpointResetTruncator>? logger = null) : IProjectionViewTruncator
{
    private readonly int _partitionCount = commitOrder.Value.PartitionCount;
    private readonly ILogger _logger = logger ?? NullLogger<ReplayCheckpointResetTruncator>.Instance;

    public async Task TruncateAllAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var names = registrations.Names;
        var grains = names
            .SelectMany(name => Enumerable.Range(0, _partitionCount).Select(partition => Reader(StoreReaderGrainKey.Of(name, partition))))
            .ToList();

        await using var hold = await StoreReaderPause.PauseAllAsync(grains, lease);
        var checkpoints = services.GetRequiredService<IProjectionCheckpointStore>();
        var reader = services.GetRequiredService<ICommittedPositionReader>().Name;
        var sagaPositions = await ReplaySagaPositions.RememberAsync(checkpoints, inner.GetType().Name, _logger, cancellationToken);
        await TruncationBetweenResets.RunAsync(
            token => Task.WhenAll(names.SelectMany(name => Enumerable.Range(0, _partitionCount)
                .Select(partition => checkpoints.ResetAsync(name, partition, reader, token)))),
            token => sagaPositions.KeepThroughAsync(inner.TruncateAllAsync, token),
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

/// <summary>
/// Keeps the positions of the saga consumers through a host's truncator, which may share a schema with them. The store
/// is listed rather than the host's sagas asked, because the replaying host need not run the sagas. Only what the
/// truncator removed or moved back is written, so a truncator that leaves them costs two reads and no write. Saga
/// readers are not paused here: a replay already suspends them, and a reader on a host that has not yet learned of
/// the replay is at worst refused its next write and reads again.
/// </summary>
internal sealed class ReplaySagaPositions
{
    private readonly IProjectionCheckpointStore _checkpoints;
    private readonly string _truncator;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<StoredCheckpoint>? _remembered;

    private ReplaySagaPositions(IProjectionCheckpointStore checkpoints, string truncator, ILogger logger, IReadOnlyList<StoredCheckpoint>? remembered)
    {
        _checkpoints = checkpoints;
        _truncator = truncator;
        _logger = logger;
        _remembered = remembered;
    }

    /// <summary>
    /// Lists the saga positions. A store that cannot list is logged and left unguarded, so that a replay that worked
    /// before keeps working; any other failure propagates, before anything has been changed.
    /// </summary>
    public static async Task<ReplaySagaPositions> RememberAsync(IProjectionCheckpointStore checkpoints, string truncator, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return new ReplaySagaPositions(checkpoints, truncator, logger, await ListAsync(checkpoints, cancellationToken));
        }
        catch (NotSupportedException)
        {
            logger.LogReplaySagaCheckpointsNotGuarded(checkpoints.GetType().Name);
            return new ReplaySagaPositions(checkpoints, truncator, logger, null);
        }
    }

    /// <summary>
    /// Runs the truncation, then writes back what it took, whether it succeeded, failed or was cancelled: the write-back
    /// is not cancelled with it, because a position left out is a saga that runs its side effects again.
    /// </summary>
    /// <exception cref="AggregateException">The truncation and the write-back both failed.</exception>
    public async Task KeepThroughAsync(Func<CancellationToken, Task> truncate, CancellationToken cancellationToken)
    {
        var truncation = await TruncationBetweenResets.CaptureAsync(() => truncate(cancellationToken));
        var writeBack = await TruncationBetweenResets.CaptureAsync(WriteBackAsync);
        if (truncation is not null && writeBack is not null)
        {
            throw new AggregateException("The truncation failed, and the saga positions it took could not all be written back.", truncation, writeBack);
        }

        if ((truncation ?? writeBack) is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    private static async Task<IReadOnlyList<StoredCheckpoint>> ListAsync(IProjectionCheckpointStore checkpoints, CancellationToken cancellationToken)
    {
        var listed = await checkpoints.ListAsync(SagaGrain.ConsumerName, cancellationToken);
        return [.. listed.Where(row => row.Consumer == SagaGrain.ConsumerName || row.Consumer.StartsWith(SagaReaderGrain.ConsumerPrefix, StringComparison.Ordinal))];
    }

    /// <summary>
    /// A row found lower counts as taken: a reader that met the missing row reads <c>0</c> and inserts again. A row further
    /// on is the reader's own progress, and one under another reader holds a position that compares with nothing.
    /// </summary>
    private async Task WriteBackAsync()
    {
        if (_remembered is not { Count: > 0 })
        {
            return;
        }

        IReadOnlyList<StoredCheckpoint> found;
        try
        {
            found = await ListAsync(_checkpoints, CancellationToken.None);
        }
        catch (Exception unlisted)
        {
            _logger.LogReplaySagaCheckpointsNotRestored(unlisted, _truncator, Describe(_remembered));
            throw;
        }

        var current = found.ToDictionary(row => (row.Consumer, row.Partition));
        var written = new List<StoredCheckpoint>();
        var unwritten = new List<StoredCheckpoint>();
        var failures = new List<Exception>();
        foreach (var position in _remembered)
        {
            var row = current.GetValueOrDefault((position.Consumer, position.Partition));
            if (row is not null && (row.Reader != position.Reader || row.Position >= position.Position))
            {
                continue;
            }

            try
            {
                await WriteAsync(position, row);
                written.Add(position);
            }
            catch (Exception failed)
            {
                unwritten.Add(position);
                failures.Add(failed);
            }
        }

        if (written.Count > 0)
        {
            _logger.LogReplaySagaCheckpointsRestored(_truncator, written.Count, Describe(written));
        }

        if (failures.Count > 0)
        {
            _logger.LogReplaySagaCheckpointsNotRestored(failures[0], _truncator, Describe(unwritten));
            ExceptionDispatchInfo.Throw(failures.Count == 1 ? failures[0] : new AggregateException("Saga positions could not be written back.", failures));
        }
    }

    private Task WriteAsync(StoredCheckpoint position, StoredCheckpoint? row) =>
        row is null
            ? _checkpoints.CreateAsync(position.Consumer, position.Partition, position.Reader, position.Position, CancellationToken.None)
            : _checkpoints.SetAsync(position.Consumer, position.Partition, position.Reader, position.Position, CancellationToken.None);

    private static string Describe(IEnumerable<StoredCheckpoint> positions) =>
        string.Join(", ", positions.Select(position => $"{position.Consumer}/{position.Partition}={position.Position}"));
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
