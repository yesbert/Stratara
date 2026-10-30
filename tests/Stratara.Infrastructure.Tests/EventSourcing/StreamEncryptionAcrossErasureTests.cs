using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Security;
using Stratara.Infrastructure.Security;
using Stratara.Testing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>data-encryption</c> → <em>Unreadable encrypted fields degrade rather than fail</em> and <em>What a stream stores
/// is encrypted field by field</em>, and <c>tenant-directory</c> → a user's own data is the user's, on the SQLite test
/// host: a stream rebuilt after its subject's erasure delivers every event with the erased fields absent, an event
/// marked as a whole is refused before it is written, and data written under the reserved default tenant follows the
/// person across tenants and dies only with the person.
/// </summary>
public class StreamEncryptionAcrossErasureTests
{
    private static readonly Guid TenantA = EventStoreTestHost.DefaultTenantId;
    private static readonly Guid TenantB = Guid.CreateVersion7();

    private sealed class Person : IAggregate
    {
        public Guid Id { get; set; }

        public string? Name { get; set; }

        public int? Age { get; set; }

        public string Language { get; set; } = string.Empty;

        public int Changes { get; set; }

        public void Apply(PersonRegistered @event)
        {
            Id = @event.PersonId;
            Name = @event.Name;
            Age = @event.Age;
            Language = @event.Language;
            Changes++;
        }

        public void Apply(PersonRenamed @event)
        {
            Name = @event.Name;
            Changes++;
        }
    }

    private sealed record PersonRegistered(
        Guid PersonId,
        [property: EncryptData] string? Name,
        [property: EncryptData] int? Age,
        string Language);

    private sealed record PersonRenamed([property: EncryptData] string? Name);

    [EncryptData]
    private sealed record PersonNotedAsAWhole(string Note);

    private static EventStoreTestHost CreateHost() =>
        EventStoreTestHost.Create(services => services
            .AddTrustedType<Person>()
            .AddTrustedType<PersonRegistered>()
            .AddTrustedType<PersonRenamed>()
            .AddTrustedType<PersonNotedAsAWhole>());

    private static SubjectEraser Eraser(EventStoreTestHost host, InMemoryTenantMembershipStore memberships) =>
        new(memberships, new InMemoryApiKeyStore(memberships), new InMemorySettingStore(),
            host.Services.GetRequiredService<IKeyStore>());

    private static async Task<List<EventStreamEntry>> EntriesAsync(EventStoreTestHost host, Guid streamId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<StrataraTestWriteDbContext>>()
            .CreateDbContextAsync();

        return await context.Set<EventStreamEntry>()
            .AsNoTracking()
            .Where(e => e.StreamId == streamId)
            .ToListAsync();
    }

    [Fact]
    public async Task A_stream_rebuilt_after_its_users_erasure_delivers_every_event_with_the_erased_fields_absent()
    {
        await using var host = CreateHost();
        var memberships = new InMemoryTenantMembershipStore();
        var user = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        await memberships.SetMembershipAsync(new TenantMembership(user, TenantA, []));
        host.Session.Set(TestSessionContext.ForTenant(TenantA, user));

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Person>(id, new PersonRegistered(id, "Ada", 36, "en"));
            await events.AppendAsync<Person>(id, new PersonRenamed("Ada Lovelace"));
            await events.SaveChangesAsync();
        });
        Assert.Equal("Ada Lovelace", (await host.AggregateAsync<Person>(id))!.Name);

        await Eraser(host, memberships).EraseUserAsync(user);

        var person = await host.AggregateAsync<Person>(id);
        Assert.NotNull(person);
        Assert.Equal(2, person!.Changes);
        Assert.Equal(id, person.Id);
        Assert.Equal("en", person.Language);
        Assert.Null(person.Name);
        Assert.Null(person.Age);
    }

    [Fact]
    public async Task An_event_marked_as_a_whole_is_refused_before_anything_is_written()
    {
        await using var host = CreateHost();
        var id = Guid.CreateVersion7();
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Person>(id, new PersonRegistered(id, "Ada", 36, "en"));
            await events.SaveChangesAsync();
        });

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Person>(id, new PersonNotedAsAWhole("kept whole"));
            await events.SaveChangesAsync();
        }));

        Assert.Contains(typeof(PersonNotedAsAWhole).FullName!, refusal.Message, StringComparison.Ordinal);
        Assert.Single(await EntriesAsync(host, id));
    }

    [Fact]
    public async Task A_persons_own_data_follows_them_across_tenants_and_dies_only_with_them()
    {
        await using var host = CreateHost();
        var memberships = new InMemoryTenantMembershipStore();
        var person = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        await memberships.SetMembershipAsync(new TenantMembership(person, TenantA, []));
        await memberships.SetMembershipAsync(new TenantMembership(person, TenantB, []));

        host.Session.Set(TestSessionContext.ForTenant(TenantA, person));
        await host.ExecuteAsync(async events =>
        {
            await events.CreateOnBehalfOfAsync<Person>(id, new PersonRegistered(id, "Ada", 36, "en"), EventSubject.ForUser(person));
            await events.SaveChangesAsync();
        });
        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Person>(id, new PersonRenamed("Ada Lovelace"));
            await events.SaveChangesAsync();
        });

        Assert.All(await EntriesAsync(host, id), e => Assert.Equal((Guid.Empty, (Guid?)person), (e.TenantId, e.UserId)));
        var scopes = await host.Services.GetRequiredService<IKeyStore>().ListScopesAsync();
        Assert.Contains(new KeyScope(DataSensitivityLevel.UserScoped, Guid.Empty.ToString("D"), person.ToString("D")), scopes);
        Assert.DoesNotContain(scopes, s => s.TenantId == TenantA.ToString("D"));

        host.Session.Set(TestSessionContext.ForTenant(TenantB, person));
        Assert.Equal("Ada Lovelace", (await host.AggregateAsync<Person>(id))!.Name);

        await Eraser(host, memberships).EraseTenantAsync(TenantA);
        Assert.Equal("Ada Lovelace", (await host.AggregateAsync<Person>(id))!.Name);

        await Eraser(host, memberships).EraseUserAsync(person);
        var erased = await host.AggregateAsync<Person>(id);
        Assert.Null(erased!.Name);
        Assert.Equal("en", erased.Language);
    }
}
