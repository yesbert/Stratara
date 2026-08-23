using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Security;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Xunit;

namespace Stratara.Testing.EntityFrameworkCore.Tests;

/// <summary>
/// The store resolves an event's subject — its data owner — before encrypting the payload, and the
/// stream's recorded owner outranks the session's. Nothing covered the case where the two differ,
/// which is the one that matters: encrypting under the session's tenant instead of the stream's
/// would put the payload beyond the reach of the stream owner's erasure.
/// </summary>
public class ResolvedSubjectEncryptionTests
{
    private static readonly Guid StreamOwner = EventStoreTestHost.DefaultTenantId;
    private static readonly Guid OtherTenant = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static EventStoreTestHost CreateHost() =>
        EventStoreTestHost.Create(s => s.AddAggregatesFromAssemblyContaining<Account>());

    [Fact]
    public async Task AnAppendIsOwnedByTheStream_NotBySessionThatWroteIt()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, StreamOwner, "Ada", 100m));
            await events.SaveChangesAsync();
        });

        host.Session.Set(TestSessionContext.ForTenant(OtherTenant));

        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Account>(id, new AccountNoteAdded("account is under review"));
            await events.SaveChangesAsync();
        });

        var entries = await EntriesAsync(host, id);
        var appended = entries.Single(e => e.EventTypeName.Contains("AccountNoteAdded", StringComparison.Ordinal));

        Assert.Equal(StreamOwner, appended.TenantId);
        Assert.NotEqual(OtherTenant, appended.TenantId);
    }

    [Fact]
    public async Task ThePayloadIsEncryptedUnderTheStreamOwnersScope()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, StreamOwner, "Ada", 100m));
            await events.SaveChangesAsync();
        });

        host.Session.Set(TestSessionContext.ForTenant(OtherTenant));

        await host.ExecuteAsync(async events =>
        {
            await events.AppendAsync<Account>(id, new AccountNoteAdded("account is under review"));
            await events.SaveChangesAsync();
        });

        var entries = await EntriesAsync(host, id);
        var appended = entries.Single(e => e.EventTypeName.Contains("AccountNoteAdded", StringComparison.Ordinal));

        var keyId = KeyIdOf(appended.DataJson);
        Assert.NotNull(keyId);

        var keys = host.Services.GetRequiredService<IKeyStore>();
        var ownerKey = await keys.GetOrCreateCurrentKeyAsync(
            new KeyScope(DataSensitivityLevel.UserScoped, StreamOwner.ToString(), null));
        var otherKey = await keys.GetOrCreateCurrentKeyAsync(
            new KeyScope(DataSensitivityLevel.UserScoped, OtherTenant.ToString(), null));

        Assert.Equal(ownerKey.KeyId, keyId);
        Assert.NotEqual(otherKey.KeyId, keyId);
    }

    [Fact]
    public async Task ThePlaintextIsNotInTheStoredPayload()
    {
        var id = Guid.CreateVersion7();
        await using var host = CreateHost();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Account>(id, new AccountOpened(id, StreamOwner, "Ada", 100m));
            await events.AppendAsync<Account>(id, new AccountNoteAdded("account is under review"));
            await events.SaveChangesAsync();
        });

        var entries = await EntriesAsync(host, id);
        var appended = entries.Single(e => e.EventTypeName.Contains("AccountNoteAdded", StringComparison.Ordinal));

        Assert.DoesNotContain("under review", appended.DataJson, StringComparison.Ordinal);
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

    private static string? KeyIdOf(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("kid", out var keyId))
            {
                return keyId.GetString();
            }
        }

        return document.RootElement.TryGetProperty("kid", out var root) ? root.GetString() : null;
    }
}
