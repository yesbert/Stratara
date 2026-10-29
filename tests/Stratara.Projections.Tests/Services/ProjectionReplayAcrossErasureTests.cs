using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Security;
using Stratara.Projections.Abstractions;
using Stratara.Shared.Partitioning;
using Stratara.Shared.Reflections;
using Stratara.Testing.EntityFrameworkCore;

namespace Stratara.Projections.Tests.Services;

/// <summary>
/// <c>data-encryption</c> → <em>Unreadable encrypted fields degrade rather than fail</em>, through a projection replay on
/// a real SQLite store: after the subject an event's fields were encrypted for has been erased, the replay hands the
/// projection every event in order, the erased fields absent and the others intact, and completes.
/// </summary>
public sealed class ProjectionReplayAcrossErasureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_replay_after_the_users_erasure_delivers_every_event_with_the_erased_fields_absent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var applied = new AppliedProfiles();
        var replay = new AwaitedReplayState();
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync(cancellationToken);
        using var host = Build(connection, applied, replay);
        await CreateSchemaAsync(host.Services, cancellationToken);

        var tenantId = Guid.NewGuid();
        var erasedUser = Guid.NewGuid();
        var keptUser = Guid.NewGuid();
        var stream = Guid.NewGuid();
        await AppendAsync(host.Services, stream, 1, new ProfileNamed("Ada", "en"), tenantId, erasedUser, cancellationToken);
        await AppendAsync(host.Services, stream, 2, new ProfileNamed("Grace", "de"), tenantId, keptUser, cancellationToken);
        await AppendAsync(host.Services, stream, 3, new ProfileNamed("Ada L.", "fr"), tenantId, erasedUser, cancellationToken);

        var keys = host.Services.GetRequiredService<IKeyStore>();
        await keys.MarkSubjectErasedAsync(KeySubject.User(erasedUser.ToString("D")), cancellationToken);
        await keys.EraseScopeAsync(
            new KeyScope(DataSensitivityLevel.UserScoped, tenantId.ToString(), erasedUser.ToString()), cancellationToken);

        await host.StartAsync(cancellationToken);
        await replay.Subscribed.WaitAsync(Timeout, cancellationToken);
        replay.RequestReplay();
        await replay.Replayed.WaitAsync(Timeout, cancellationToken);
        await host.StopAsync(cancellationToken);

        Assert.Null(replay.Failure);
        Assert.Equal(
            [(1L, null, "en"), (2L, "Grace", "de"), (3L, null, "fr")],
            applied.ToList().OrderBy(p => p.Version));
    }

    private static IHost Build(SqliteConnection connection, AppliedProfiles applied, AwaitedReplayState replay)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Services
            .AddLogging()
            .AddStrataraTestingEventStore<StrataraTestWriteDbContext>(connection, Guid.NewGuid())
            .AddResiliencePipelines()
            .AddProjectionHandling(builder.Configuration)
            .AddTrustedType<ProfileNamed>()
            .AddSingleton(applied)
            .AddSingleton<IProjectionReplayState>(replay)
            .AddScoped<IProjection, ProfileProjection>()
            .AddScoped<IProjectionViewTruncator, AppliedProfilesTruncator>();
        return builder.Build();
    }

    private static async Task CreateSchemaAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>().CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static async Task AppendAsync(IServiceProvider services, Guid stream, long version, ProfileNamed @event,
        Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var serializer = services.GetRequiredService<ISecureJsonSerializer>();
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>().CreateDbContextAsync(cancellationToken);
        context.Set<EventStreamEntry>().Add(new EventStreamEntry
        {
            Id = Guid.CreateVersion7(),
            StreamId = stream,
            Version = version,
            EventTypeName = typeof(ProfileNamed).GetQualifiedTypeName(),
            AggregateTypeName = "Stratara.Projections.Tests.ProfileAggregate",
            DataJson = await serializer.SerializeAsync(@event, tenantId, userId, cancellationToken),
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = Guid.CreateVersion7().ToString("N"),
            CausationId = Guid.CreateVersion7().ToString("N"),
            BucketId = BucketCalculator.GetBucketId(stream),
            TenantId = tenantId,
            UserId = userId,
            ActorTenantId = tenantId,
            ActorUserId = userId,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private sealed record ProfileNamed([property: EncryptData] string? Name, string Language);

    private sealed class ProfileProjection(AppliedProfiles applied) : IProjection
    {
        private Task HandleAsync(IEvent<ProfileNamed> @event, CancellationToken cancellationToken)
        {
            applied.Add((@event.Version, @event.Data.Name, @event.Data.Language));
            return Task.CompletedTask;
        }
    }

    private sealed class AppliedProfiles
    {
        private readonly ConcurrentQueue<(long Version, string? Name, string Language)> _profiles = new();

        public void Add((long Version, string? Name, string Language) profile) => _profiles.Enqueue(profile);

        public void Clear() => _profiles.Clear();

        public List<(long Version, string? Name, string Language)> ToList() => [.. _profiles];
    }

    private sealed class AppliedProfilesTruncator(AppliedProfiles applied) : IProjectionViewTruncator
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
