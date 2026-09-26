using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Domain;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>aggregate-rehydration</c> → <em>An unhandled event is skipped rather than rejected</em>, on the
/// SQLite test host: an event the aggregate declares no handler for is skipped without being read, so
/// neither its registration nor its key decides whether the stream can be rebuilt.
/// </summary>
public class UnhandledEventRehydrationTests
{
    private static readonly Guid Tenant = EventStoreTestHost.DefaultTenantId;

    private sealed class Customer : IAggregate
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Closed { get; set; }

        public void Apply(CustomerOpened @event)
        {
            Id = @event.CustomerId;
            Name = @event.Name;
        }

        public void Apply(CustomerRenamed @event) => Name = @event.Name;

        public void Apply(IEvent<CustomerClosed> @event) => Closed = true;
    }

    private record CustomerOpened(Guid CustomerId, Guid TenantId, string Name) : IAggregateCreationEvent;

    private sealed record CustomerRenamed(string Name);

    private sealed record CustomerRetitled(string Title);

    private sealed class RetitledUpcaster : IEventUpcaster
    {
        public string SourceEventTypeName => typeof(CustomerRetitled).AssemblyQualifiedName!;

        public string TargetEventTypeName => typeof(CustomerRenamed).AssemblyQualifiedName!;

        public JsonNode Upcast(JsonNode payload) => new JsonObject { ["Name"] = payload["Title"]?.GetValue<string>() };
    }

    private sealed record CustomerClosed(DateTimeOffset ClosedAt);

    private sealed record CustomerNoteRetired(string Note);

    [EncryptData(DataSensitivityLevel.TenantScoped)]
    private sealed record CustomerSecretNoted(string Secret);

    private sealed class AlwaysSnapshotStrategy : ISnapshotStrategy
    {
        public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion) => true;
    }

    private static EventStoreTestHost CreateHost(Action<IServiceCollection>? configure = null) =>
        EventStoreTestHost.Create(s =>
        {
            s.AddAggregatesFromAssemblyContaining<UnhandledEventRehydrationTests>();
            configure?.Invoke(s);
        });

    [Fact]
    public async Task A_cascade_event_the_host_never_registered_does_not_stop_the_rebuild()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Customer>(id, new CustomerOpened(id, Tenant, "Ada"));
            await events.AppendAsync<Customer>(id, new CustomerTenantsDeleted(id, [Guid.CreateVersion7()], DateTimeOffset.UtcNow));
            await events.AppendAsync<Customer>(id, new CustomerClosed(DateTimeOffset.UtcNow));
            await events.SaveChangesAsync();
        });

        var customer = await host.AggregateAsync<Customer>(id);

        Assert.NotNull(customer);
        Assert.Equal("Ada", customer!.Name);
        Assert.True(customer.Closed);
    }

    [Fact]
    public async Task A_retired_event_type_does_not_stop_the_rebuild()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Customer>(id, new CustomerOpened(id, Tenant, "Ada"));
            await events.AppendAsync<Customer>(id, new CustomerNoteRetired("no longer of interest"));
            await events.AppendAsync<Customer>(id, new CustomerClosed(DateTimeOffset.UtcNow));
            await events.SaveChangesAsync();
        });

        var customer = await host.AggregateAsync<Customer>(id);

        Assert.NotNull(customer);
        Assert.Equal("Ada", customer!.Name);
        Assert.True(customer.Closed);
    }

    [Fact]
    public async Task A_due_snapshot_does_not_fail_the_save_over_an_unregistered_unhandled_event()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost(s => s.AddSingleton<ISnapshotStrategy>(new AlwaysSnapshotStrategy()));

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Customer>(id, new CustomerOpened(id, Tenant, "Ada"));
            await events.AppendAsync<Customer>(id, new CustomerNoteRetired("no longer of interest"));
            await events.SaveChangesAsync();
        });

        Assert.NotEmpty(await SnapshotsAsync(host, id));

        var customer = await host.AggregateAsync<Customer>(id);

        Assert.NotNull(customer);
        Assert.Equal("Ada", customer!.Name);
    }

    [Fact]
    public async Task An_unhandled_event_whose_key_is_gone_does_not_stop_the_rebuild()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost(s => s.AddTrustedType<CustomerSecretNoted>());

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Customer>(id, new CustomerOpened(id, Tenant, "Ada"));
            await events.AppendAsync<Customer>(id, new CustomerSecretNoted("held under its own key"));
            await events.AppendAsync<Customer>(id, new CustomerClosed(DateTimeOffset.UtcNow));
            await events.SaveChangesAsync();
        });

        var secret = (await EntriesAsync(host, id))
            .Single(e => e.EventTypeName.Contains(nameof(CustomerSecretNoted), StringComparison.Ordinal));
        var keyId = KeyIdOf(secret.DataJson);
        Assert.NotNull(keyId);
        await host.Services.GetRequiredService<IKeyStore>().RevokeAsync(keyId!);

        var customer = await host.AggregateAsync<Customer>(id);

        Assert.NotNull(customer);
        Assert.Equal("Ada", customer!.Name);
        Assert.True(customer.Closed);
    }

    [Fact]
    public async Task An_event_upcast_into_a_handled_type_is_applied()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost(s => s.AddEventUpcaster(new RetitledUpcaster()));

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Customer>(id, new CustomerOpened(id, Tenant, "Ada"));
            await events.AppendAsync<Customer>(id, new CustomerRetitled("Ada Lovelace"));
            await events.SaveChangesAsync();
        });

        var customer = await host.AggregateAsync<Customer>(id);

        Assert.NotNull(customer);
        Assert.Equal("Ada Lovelace", customer!.Name);
    }

    [Fact]
    public async Task A_handled_event_recorded_under_another_namespace_fails_the_rebuild_naming_it()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Customer>(id, new CustomerOpened(id, Tenant, "Ada"));
            await events.AppendAsync<Customer>(id, new CustomerClosed(DateTimeOffset.UtcNow));
            await events.SaveChangesAsync();
        });

        const string movedName = "Former.Namespace.CustomerClosed, Former.Assembly";
        await RenameRecordedTypeAsync(host, id, nameof(CustomerClosed), movedName);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.AggregateAsync<Customer>(id));

        Assert.Contains(movedName, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_trusts_the_payload_of_a_handler_taking_the_enveloped_event()
    {
        var services = new ServiceCollection();
        services.AddAggregatesFromAssemblyContaining<UnhandledEventRehydrationTests>();
        using var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredService<ITrustedTypeResolver>();

        Assert.True(resolver.TryResolve(typeof(CustomerClosed).AssemblyQualifiedName!, out var resolved));
        Assert.Equal(typeof(CustomerClosed), resolved);
    }

    private static async Task RenameRecordedTypeAsync(EventStoreTestHost host, Guid streamId, string typeName, string recordedName)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>()
            .CreateDbContextAsync();

        var entries = await context.Set<EventStreamEntry>()
            .Where(e => e.StreamId == streamId)
            .ToListAsync();
        entries.Single(e => e.EventTypeName.Contains(typeName, StringComparison.Ordinal)).EventTypeName = recordedName;
        await context.SaveChangesAsync();
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
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync();
    }

    private static async Task<List<Snapshot>> SnapshotsAsync(EventStoreTestHost host, Guid streamId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>()
            .CreateDbContextAsync();

        return await context.Set<Snapshot>()
            .AsNoTracking()
            .Where(s => s.StreamId == streamId)
            .ToListAsync();
    }

    private static string? KeyIdOf(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return document.RootElement.TryGetProperty("kid", out var keyId) ? keyId.GetString() : null;
    }
}
