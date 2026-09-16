using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.Outbox;
using Stratara.Contracts.Messages;
using Stratara.Orleans.EntityFrameworkCore.Intents;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Shared.Reflections;

namespace Stratara.Orleans.IntegrationTests.Outbox;

/// <summary>
/// A command the execution model records is stored under a kind of its own, so a bus drain reading stored
/// commands never sees it, while the intent store still resumes a command recorded under the command envelope's
/// kind before the kinds were told apart. A kept command is not due.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class RecordedCommandKindTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_recorded_command_kind";
    private static readonly CommandEnvelope Envelope = new(Guid.NewGuid(), "{}", "Probe", "{}");

    [Fact]
    public async Task A_recorded_command_is_invisible_to_the_bus_drain_and_due_for_the_intent_store()
    {
        await using var store = await PocStore<PocWriteDbContext>.CreateAsync(postgres.ConnectionStringFor(Database));
        await ClearOutboxAsync(store);
        var intents = new CommandIntentStore<PocWriteDbContext>(store.ContextFactory);
        var recorded = Guid.CreateVersion7();

        await intents.RecordAsync(recorded, Envelope, Guid.NewGuid(), heavy: false, TestContext.Current.CancellationToken);

        Assert.Empty(await ReadAsBusAsync(store));
        var due = await intents.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10, TestContext.Current.CancellationToken);
        Assert.Equal([recorded], due.Select(intent => intent.Id));
    }

    [Fact]
    public async Task A_command_recorded_under_the_previous_kind_is_still_resumed_and_a_kept_one_is_not_due()
    {
        await using var store = await PocStore<PocWriteDbContext>.CreateAsync(postgres.ConnectionStringFor(Database));
        await ClearOutboxAsync(store);
        var intents = new CommandIntentStore<PocWriteDbContext>(store.ContextFactory);
        var previous = Guid.CreateVersion7();
        var kept = Guid.CreateVersion7();
        await using (var context = await store.CreateContextAsync())
        {
            context.Set<OutboxEntry>().Add(new OutboxEntry
            {
                Id = previous,
                BucketId = 0,
                DataJson = System.Text.Json.JsonSerializer.Serialize(Envelope),
                DataTypeName = typeof(CommandEnvelope).GetQualifiedTypeName(),
                Timestamp = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await intents.RecordAsync(kept, Envelope, null, heavy: false, TestContext.Current.CancellationToken);
        await intents.KeepAsync(kept, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var due = await intents.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1), 10, TestContext.Current.CancellationToken);
        Assert.Equal([previous], due.Select(intent => intent.Id));
    }

    /// <summary>What the bus drain reads for stored commands: every outbox entry of the command envelope's kind.</summary>
    private static async Task<List<OutboxEntry>> ReadAsBusAsync(PocStore<PocWriteDbContext> store)
    {
        var kind = typeof(CommandEnvelope).GetQualifiedTypeName();
        await using var context = await store.CreateContextAsync();
        return await context.Set<OutboxEntry>().AsNoTracking().Where(o => o.DataTypeName == kind).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ClearOutboxAsync(PocStore<PocWriteDbContext> store)
    {
        await using var context = await store.CreateContextAsync();
        await context.Set<OutboxEntry>().ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }
}
