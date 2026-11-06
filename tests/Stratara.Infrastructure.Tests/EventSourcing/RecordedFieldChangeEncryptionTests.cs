using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Commands;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Infrastructure.Security;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.EventSourcing.Extensions;
using Stratara.Shared.Partitioning;
using Stratara.Testing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>data-encryption</c> → <em>A recorded field change is protected as the property it names</em> and
/// <em>Field changes recorded in the clear are read as they are and not rewritten</em>, and
/// <c>update-change-tracking</c> → <em>A field-changed event names the field and carries its new value</em>: a change
/// set applied through the real handler, stored through the real serializer on the SQLite test store, and replayed.
/// </summary>
public class RecordedFieldChangeEncryptionTests
{
    private const string SubmittedEmail = "ada@secret.example";
    private const string SubmittedCity = "Berlin";

    private static readonly Guid Tenant = EventStoreTestHost.DefaultTenantId;

    public sealed class Customer
    {
        public Guid Id { get; set; }

        [EncryptData]
        public string? Email { get; set; }

        public string? City { get; set; }

        public int Notes { get; set; }

        public void Apply(CustomerOpened e)
        {
            Id = e.CustomerId;
            City = e.City;
        }

        public void Apply(FieldChangedEvent<Customer> e) => this.ApplyPropertyChanged(e);

        public void Apply(CustomerNoted e) => Notes++;
    }

    public sealed record CustomerOpened(Guid CustomerId, string City);

    public sealed record CustomerNoted(int Page);

    public sealed record UpdateCustomer(Guid AggregateId, long SourceVersion, string? Email, string? City) : IUpdateCommand;

    private sealed class SwitchableSnapshotStrategy : ISnapshotStrategy
    {
        public bool Enabled { get; set; }

        public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion) => Enabled;
    }

    private static EventStoreTestHost CreateHost(SwitchableSnapshotStrategy? strategy = null) =>
        EventStoreTestHost.Create(services =>
        {
            services
                .AddLogging()
                .AddTrustedType<Customer>()
                .AddTrustedType<CustomerOpened>()
                .AddTrustedType<CustomerNoted>()
                .AddTrustedType<FieldChangedEvent<Customer>>();
            if (strategy is not null)
            {
                services.AddSingleton<ISnapshotStrategy>(strategy);
            }
        });

    private static SubjectEraser Eraser(EventStoreTestHost host, InMemoryTenantMembershipStore memberships) =>
        new(memberships, new InMemoryApiKeyStore(memberships), new InMemorySettingStore(),
            host.Services.GetRequiredService<IKeyStore>());

    private static async Task<InMemoryTenantMembershipStore> MemberOfTenantAsync(EventStoreTestHost host, Guid user)
    {
        var memberships = new InMemoryTenantMembershipStore();
        await memberships.SetMembershipAsync(new TenantMembership(user, Tenant, []));
        host.Session.Set(TestSessionContext.ForTenant(Tenant, user));
        return memberships;
    }

    private static async Task OpenAsync(EventStoreTestHost host, Guid id) =>
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Customer>(id, new CustomerOpened(id, "Paris"));
            await events.SaveChangesAsync();
        });

    private static async Task ApplyChangeSetAsync(EventStoreTestHost host, Guid id, long sourceVersion)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IChangeSetHandler>();
        var changes = await handler.CreateChangeSetAsync<Customer, UpdateCustomer>(
            new UpdateCustomer(id, sourceVersion, SubmittedEmail, SubmittedCity));
        await handler.ApplyChangeSetAsync<Customer>(id, changes);
    }

    private static async Task<List<EventStreamEntry>> EntriesAsync(EventStoreTestHost host, Guid streamId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>()
            .CreateDbContextAsync();

        return await context.Set<EventStreamEntry>()
            .AsNoTracking()
            .Where(e => e.StreamId == streamId)
            .OrderBy(e => e.Version)
            .ToListAsync();
    }

    private static EventStreamEntry RecordedChangeOf(IEnumerable<EventStreamEntry> entries, string propertyName) =>
        entries.Single(e =>
            e.EventTypeName.Contains(nameof(FieldChangedEvent<>), StringComparison.Ordinal)
            && JsonDocument.Parse(e.DataJson).RootElement.GetProperty("PropertyName").GetString() == propertyName);

    private static async Task<Snapshot?> LatestSnapshotAsync(EventStoreTestHost host, Guid streamId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();
        return await unitOfWork.CreateSnapshotRepository(transaction)
            .GetAsync(streamId, typeof(Customer).AssemblyQualifiedName!);
    }

    [Fact]
    public async Task A_change_set_stores_a_marked_value_as_ciphertext_and_replays_both_values()
    {
        await using var host = CreateHost();
        var id = Guid.CreateVersion7();
        await MemberOfTenantAsync(host, Guid.CreateVersion7());
        await OpenAsync(host, id);

        await ApplyChangeSetAsync(host, id, sourceVersion: 1);

        var entries = await EntriesAsync(host, id);
        var marked = RecordedChangeOf(entries, nameof(Customer.Email));
        Assert.DoesNotContain(SubmittedEmail, marked.DataJson, StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(marked.DataJson).RootElement.GetProperty("NewValue").TryGetProperty("__enc", out _));
        Assert.Contains(SubmittedCity, RecordedChangeOf(entries, nameof(Customer.City)).DataJson, StringComparison.Ordinal);

        var rebuilt = await host.AggregateAsync<Customer>(id);
        Assert.Equal((SubmittedEmail, SubmittedCity), (rebuilt!.Email, rebuilt.City));
    }

    [Fact]
    public async Task A_change_appended_directly_is_protected_like_one_from_a_change_set()
    {
        await using var host = CreateHost();
        var id = Guid.CreateVersion7();
        await OpenAsync(host, id);

        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Customer>(id, new FieldChangedEvent<Customer>(nameof(Customer.Email), SubmittedEmail));
            await events.SaveChangesAsync();
        });

        Assert.DoesNotContain(
            SubmittedEmail, RecordedChangeOf(await EntriesAsync(host, id), nameof(Customer.Email)).DataJson, StringComparison.Ordinal);
        Assert.Equal(SubmittedEmail, (await host.AggregateAsync<Customer>(id))!.Email);
    }

    [Fact]
    public async Task The_published_bundle_carries_the_marked_value_as_ciphertext()
    {
        await using var host = CreateHost();
        var id = Guid.CreateVersion7();
        await OpenAsync(host, id);

        await ApplyChangeSetAsync(host, id, sourceVersion: 1);

        var published = host.Outbox.Bundles.SelectMany(bundle => bundle.Events).Select(message => message.DataJson).ToList();
        Assert.Contains(published, json => json.Contains(nameof(Customer.Email), StringComparison.Ordinal));
        Assert.DoesNotContain(published, json => json.Contains(SubmittedEmail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task After_the_owner_is_erased_the_marked_value_is_absent_and_the_replay_continues()
    {
        await using var host = CreateHost();
        var id = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var memberships = await MemberOfTenantAsync(host, user);
        await OpenAsync(host, id);
        await ApplyChangeSetAsync(host, id, sourceVersion: 1);
        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Customer>(id, new CustomerNoted(1));
            await events.SaveChangesAsync();
        });
        var before = await EntriesAsync(host, id);

        await Eraser(host, memberships).EraseUserAsync(user);

        var rebuilt = await host.AggregateAsync<Customer>(id);
        Assert.NotNull(rebuilt);
        Assert.Null(rebuilt.Email);
        Assert.Equal(SubmittedCity, rebuilt.City);
        Assert.Equal(1, rebuilt.Notes);
        Assert.Equal(before.Select(e => e.DataJson), (await EntriesAsync(host, id)).Select(e => e.DataJson));
    }

    [Fact]
    public async Task A_change_recorded_in_the_clear_replays_as_stored_and_stays_as_it_was()
    {
        await using var host = CreateHost();
        var id = Guid.CreateVersion7();
        await OpenAsync(host, id);
        var earlierJson = JsonSerializer.Serialize(new FieldChangedEvent<Customer>(nameof(Customer.Email), "earlier@clear.example"));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>()
                .CreateDbContextAsync();
            var opening = (await EntriesAsync(host, id)).Single();
            context.Set<EventStreamEntry>().Add(new EventStreamEntry
            {
                Id = Guid.CreateVersion7(),
                StreamId = id,
                Version = 2,
                BucketId = BucketCalculator.GetBucketId(id),
                TenantId = opening.TenantId,
                UserId = opening.UserId,
                ActorTenantId = opening.ActorTenantId,
                ActorUserId = opening.ActorUserId,
                CorrelationId = opening.CorrelationId,
                CausationId = opening.CausationId,
                EventTypeName = typeof(FieldChangedEvent<Customer>).AssemblyQualifiedName!,
                AggregateTypeName = opening.AggregateTypeName,
                DataJson = earlierJson,
                Timestamp = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        Assert.Equal("earlier@clear.example", (await host.AggregateAsync<Customer>(id))!.Email);

        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Customer>(id, new FieldChangedEvent<Customer>(nameof(Customer.Email), SubmittedEmail));
            await events.SaveChangesAsync();
        });

        Assert.Equal(SubmittedEmail, (await host.AggregateAsync<Customer>(id))!.Email);
        var entries = await EntriesAsync(host, id);
        Assert.Equal(earlierJson, entries[1].DataJson);
        Assert.DoesNotContain(SubmittedEmail, entries[2].DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rebuild_from_the_snapshot_and_from_the_events_agree_before_and_after_erasure()
    {
        var strategy = new SwitchableSnapshotStrategy();
        await using var host = CreateHost(strategy);
        var fromEvents = Guid.CreateVersion7();
        var fromSnapshot = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var memberships = await MemberOfTenantAsync(host, user);
        await OpenAsync(host, fromEvents);
        await OpenAsync(host, fromSnapshot);
        await ApplyChangeSetAsync(host, fromEvents, sourceVersion: 1);

        strategy.Enabled = true;
        await ApplyChangeSetAsync(host, fromSnapshot, sourceVersion: 1);
        strategy.Enabled = false;

        var snapshot = await LatestSnapshotAsync(host, fromSnapshot);
        Assert.Equal(3, snapshot?.Version);
        Assert.Null(await LatestSnapshotAsync(host, fromEvents));
        Assert.Equal(
            KeyIdOf(JsonDocument.Parse(snapshot!.DataJson).RootElement.GetProperty(nameof(Customer.Email))),
            KeyIdOf(JsonDocument.Parse(RecordedChangeOf(await EntriesAsync(host, fromEvents), nameof(Customer.Email)).DataJson).RootElement.GetProperty("NewValue")));

        Assert.Equal(SubmittedEmail, (await host.AggregateAsync<Customer>(fromEvents))!.Email);
        Assert.Equal(SubmittedEmail, (await host.AggregateAsync<Customer>(fromSnapshot))!.Email);

        await Eraser(host, memberships).EraseUserAsync(user);

        var erasedFromEvents = await host.AggregateAsync<Customer>(fromEvents);
        var erasedFromSnapshot = await host.AggregateAsync<Customer>(fromSnapshot);
        Assert.Null(erasedFromEvents!.Email);
        Assert.Null(erasedFromSnapshot!.Email);
        Assert.Equal(erasedFromEvents.City, erasedFromSnapshot.City);
    }

    private static string? KeyIdOf(JsonElement envelope) => envelope.GetProperty("kid").GetString();
}
