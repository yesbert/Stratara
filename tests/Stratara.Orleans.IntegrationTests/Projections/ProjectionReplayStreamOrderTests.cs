using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// <c>projections</c> → <em>A replay applies each stream in the order it was written</em>, through the replay itself
/// on PostgreSQL. Each entry is appended by a save of its own in reverse version order, so a stream's sequence numbers
/// run against its versions exactly as they do where one save's statement order put them there. A replay requested
/// through <see cref="IProjectionReplayState"/> on a host wired with the projection runtime and the PostgreSQL write
/// store, with a batch size that ends the first batch inside the inverted run, hands the projection each stream in
/// version order and every entry once, and extends that batch to hold the run.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ProjectionReplayStreamOrderTests(PostgreSqlFixture postgres)
{
    private const int BatchSize = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_replay_over_a_stream_numbered_against_its_versions_applies_it_in_version_order_across_a_batch_boundary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = postgres.ConnectionStringFor("poc_replay_worker_stream_order");
        await using var store = await PocStore<PocCommitOrderWriteDbContext>.CreateAsync(connectionString, maintainCounter: false);
        await using (var context = await store.CreateContextAsync())
        {
            await context.Set<EventStreamEntry>().ExecuteDeleteAsync(cancellationToken);
        }

        var applied = new AppliedEntries();
        var replay = new AwaitedReplayState();
        using var host = Build(connectionString, applied, replay);

        var stream = Guid.NewGuid();
        var other = Guid.NewGuid();
        var written = await AppendAsync(store, host.Services, cancellationToken, (other, 1), (stream, 3), (stream, 2), (stream, 1), (other, 2));

        using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startTimeout.CancelAfter(Timeout);
        await host.StartAsync(startTimeout.Token);
        await replay.Subscribed.WaitAsync(Timeout, cancellationToken);
        replay.RequestReplay();
        await replay.Replayed.WaitAsync(Timeout, cancellationToken);
        await host.StopAsync(cancellationToken);

        var received = applied.ToList();
        Assert.Null(replay.Failure);
        Assert.Equal(written.Order(), received.Select(e => e.Id).Order());
        Assert.Equal([1L, 2L, 3L], received.Where(e => e.StreamId == stream).Select(e => e.Version));
        Assert.Equal([1L, 2L], received.Where(e => e.StreamId == other).Select(e => e.Version));
        Assert.Equal([0L, 4L, 5L], replay.Progress);
    }

    private static IHost Build(string connectionString, AppliedEntries applied, AwaitedReplayState replay)
    {
        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = connectionString,
            [$"{ProjectionOptions.SectionName}:{nameof(ProjectionOptions.BatchSize)}"] = BatchSize.ToString(CultureInfo.InvariantCulture),
        });
        builder.Services
            .AddSessionContext()
            .AddSecurity()
            .AddMapping()
            .AddResiliencePipelines()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddProjectionHandling(builder.Configuration)
            .AddTrustedType<StepTaken>()
            .AddSingleton(applied)
            .AddSingleton<IProjectionReplayState>(replay)
            .AddScoped<IProjection, StreamOrderProjection>()
            .AddScoped<IProjectionViewTruncator, AppliedEntriesTruncator>();
        return builder.Build();
    }

    private static async Task<List<Guid>> AppendAsync(
        PocStore<PocCommitOrderWriteDbContext> store,
        IServiceProvider services,
        CancellationToken cancellationToken,
        params (Guid Stream, long Version)[] entries)
    {
        var tenantId = Guid.NewGuid();
        var serializer = services.GetRequiredService<ISecureJsonSerializer>();
        var written = new List<Guid>();
        foreach (var (stream, version) in entries)
        {
            await using var context = await store.CreateContextAsync();
            var entry = PocStore<PocCommitOrderWriteDbContext>.NewEntry(stream, version, BucketCalculator.GetBucketId(stream), tenantId);
            entry.EventTypeName = typeof(StepTaken).GetQualifiedTypeName();
            entry.DataJson = await serializer.SerializeAsync(new StepTaken(version), tenantId, cancellationToken: cancellationToken);
            context.Set<EventStreamEntry>().Add(entry);
            await context.SaveChangesAsync(cancellationToken);
            written.Add(entry.Id);
        }

        return written;
    }

    private sealed record StepTaken(long Step);

    private sealed class StreamOrderProjection(AppliedEntries applied) : IProjection
    {
        private Task HandleAsync(IEvent<StepTaken> @event, CancellationToken cancellationToken)
        {
            applied.Add(@event);
            return Task.CompletedTask;
        }
    }

    /// <summary>The projection's read model: the entries it was handed, in the order it was handed them.</summary>
    private sealed class AppliedEntries
    {
        private readonly ConcurrentQueue<(Guid Id, Guid StreamId, long Version)> _entries = new();

        public void Add(IEvent @event) => _entries.Enqueue((@event.Id, @event.StreamId, @event.Version));

        public void Clear() => _entries.Clear();

        public List<(Guid Id, Guid StreamId, long Version)> ToList() => [.. _entries];
    }

    private sealed class AppliedEntriesTruncator(AppliedEntries applied) : IProjectionViewTruncator
    {
        public Task TruncateAllAsync(CancellationToken cancellationToken = default)
        {
            applied.Clear();
            return Task.CompletedTask;
        }
    }

    /// <summary>A replay state in this process that lets the test wait for the replay it requested to end.</summary>
    private sealed class AwaitedReplayState : IProjectionReplayState
    {
        private readonly TaskCompletionSource<Func<Task>> _subscriber = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<long> _progress = new();

        public Task Subscribed => _subscriber.Task;

        public Task Replayed { get; private set; } = Task.CompletedTask;

        public string? Failure { get; private set; }

        public IReadOnlyList<long> Progress => [.. _progress];

        public bool IsReplayActive { get; private set; }

        public void Activate() => IsReplayActive = true;

        public void Deactivate() => IsReplayActive = false;

        public void SetFailed(string errorMessage) => Failure = errorMessage;

        public void SetProgress(long processedEvents, long totalEvents) => _progress.Enqueue(processedEvents);

        public ReplayProgress GetProgress() => new(IsReplayActive, _progress.LastOrDefault(), 0, 0, Failure);

        public Task SubscribeToReplayRequestAsync(Func<Task> onReplayRequested, CancellationToken cancellationToken = default)
        {
            _subscriber.TrySetResult(onReplayRequested);
            return Task.CompletedTask;
        }

        public void RequestReplay()
        {
            var subscriber = _subscriber.Task.IsCompletedSuccessfully
                ? _subscriber.Task.Result
                : throw new InvalidOperationException("The replay worker has not subscribed to replay requests.");
            Replayed = Task.Run(subscriber);
        }
    }
}
