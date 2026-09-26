using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Testing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>aggregate-rehydration</c> → a snapshot is protected under its stream's recorded owner, tenant and user, so every
/// erasure that reaches the stream's events reaches its snapshots too. Against the SQLite test store, whose key store
/// is in memory and whose serializer is the real one.
/// </summary>
public class SnapshotOwnerTests
{
    public sealed class Diary
    {
        [EncryptData]
        public string? Entry { get; set; }

        public int Pages { get; set; }

        public void Apply(DiaryOpened e) => Entry = e.Entry;

        public void Apply(DiaryPageAdded e) => Pages++;
    }

    public sealed record DiaryOpened(string Entry);

    public sealed record DiaryPageAdded(int Page);

    private sealed class SwitchableSnapshotStrategy : ISnapshotStrategy
    {
        public bool Enabled { get; set; } = true;

        public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion) => Enabled;
    }

    private static readonly Guid Tenant = EventStoreTestHost.DefaultTenantId;

    private static EventStoreTestHost CreateHost(SwitchableSnapshotStrategy strategy) =>
        EventStoreTestHost.Create(services => services
            .AddTrustedType<Diary>()
            .AddTrustedType<DiaryOpened>()
            .AddTrustedType<DiaryPageAdded>()
            .AddSingleton<ISnapshotStrategy>(strategy));

    private static async Task<Snapshot?> LatestSnapshotAsync(EventStoreTestHost host, Guid streamId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();
        return await unitOfWork.CreateSnapshotRepository(transaction)
            .GetAsync(streamId, typeof(Diary).AssemblyQualifiedName!);
    }

    [Fact]
    public async Task A_users_erasure_reaches_the_snapshot_of_that_users_aggregate()
    {
        await using var host = CreateHost(new SwitchableSnapshotStrategy());
        var streamId = Guid.CreateVersion7();
        var owningUser = Guid.NewGuid();

        host.Session.Set(TestSessionContext.ForTenant(Tenant, owningUser));
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Diary>(streamId, new DiaryOpened("secret"));
            await events.SaveChangesAsync();
        });
        Assert.Equal("secret", (await host.AggregateAsync<Diary>(streamId))?.Entry);

        await host.Services.GetRequiredService<IKeyStore>().EraseScopeAsync(
            new KeyScope(DataSensitivityLevel.UserScoped, Tenant.ToString("D"), owningUser.ToString("D")));

        var rebuilt = await host.AggregateAsync<Diary>(streamId);
        Assert.NotNull(rebuilt);
        Assert.Null(rebuilt.Entry);
        Assert.Equal(owningUser, (await LatestSnapshotAsync(host, streamId))?.UserId);
    }

    [Fact]
    public async Task A_snapshot_triggered_by_a_stated_subject_is_kept_under_the_streams_owner()
    {
        var strategy = new SwitchableSnapshotStrategy { Enabled = false };
        await using var host = CreateHost(strategy);
        var streamId = Guid.CreateVersion7();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Diary>(streamId, new DiaryOpened("secret"));
            await events.SaveChangesAsync();
        });

        strategy.Enabled = true;
        await host.ExecuteAsync(async events =>
        {
            await events.AppendOnBehalfOfAsync<Diary>(streamId, new DiaryPageAdded(1), new EventSubject(Guid.NewGuid()));
            await events.SaveChangesAsync();
        });

        var snapshot = await LatestSnapshotAsync(host, streamId);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(Tenant, snapshot.TenantId);
        Assert.Equal("secret", (await host.AggregateAsync<Diary>(streamId))?.Entry);
    }

    [Fact]
    public async Task A_snapshot_written_before_its_user_was_recorded_is_read_under_its_tenant()
    {
        await using var host = CreateHost(new SwitchableSnapshotStrategy { Enabled = false });
        var streamId = Guid.CreateVersion7();
        var owningUser = Guid.NewGuid();

        host.Session.Set(TestSessionContext.ForTenant(Tenant, owningUser));
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Diary>(streamId, new DiaryOpened("secret"));
            await events.SaveChangesAsync();
        });

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var serializer = scope.ServiceProvider.GetRequiredService<ISecureJsonSerializer>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
            await using var transaction = await unitOfWork.StartAsync();
            await unitOfWork.CreateSnapshotRepository(transaction).AddAsync(new Snapshot
            {
                Id = Guid.CreateVersion7(),
                StreamId = streamId,
                Version = 1,
                AggregateTypeName = typeof(Diary).AssemblyQualifiedName!,
                DataJson = await serializer.SerializeAsync(new Diary { Entry = "from before", Pages = 7 }, Tenant),
                TenantId = Tenant,
                BucketId = 0,
                Timestamp = DateTimeOffset.UtcNow
            });
            await transaction.SaveChangesAsync();
        }

        var rebuilt = await host.AggregateAsync<Diary>(streamId);
        Assert.Equal(("from before", 7), (rebuilt?.Entry, rebuilt?.Pages ?? 0));
    }
}
