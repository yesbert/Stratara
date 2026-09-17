using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stratara.Abstractions.Outbox;
using Stratara.Contracts.Messages;
using Stratara.Orleans.EntityFrameworkCore.Intents;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// The intent store claims a batch of due commands in a number of statements that does not grow with the batch: every
/// row is stamped once with its attempt counted, and a row a concurrent claimer stamped after the read is left to it.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class IntentClaimTests(PostgreSqlFixture postgres)
{
    private const string Database = "poc_intent_claim";
    private const int Rows = 100;

    [Fact]
    public async Task A_hundred_due_rows_are_claimed_in_a_handful_of_statements_and_a_concurrent_claim_is_respected()
    {
        await using var store = await PocStore<PocWriteDbContext>.CreateAsync(postgres.ConnectionStringFor(Database));
        await using (var context = await store.CreateContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM outbox_entry", TestContext.Current.CancellationToken);
        }

        var intents = new CommandIntentStore<PocWriteDbContext>(store.ContextFactory);
        for (var i = 0; i < Rows; i++)
        {
            var id = Guid.CreateVersion7();
            await intents.RecordAsync(id, new CommandEnvelope(id, "{}", "Probe", "{}"), Guid.NewGuid(), heavy: false, TestContext.Current.CancellationToken);
        }

        var due = await intents.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1), Rows, TestContext.Current.CancellationToken);
        Assert.Equal(Rows, due.Count);
        var taken = due[17];
        Assert.True(await intents.TryClaimAsync(taken.Id, taken.LastHandedOverAt, DateTimeOffset.UtcNow.AddSeconds(-1), TestContext.Current.CancellationToken));

        int statements;
        IReadOnlyList<Guid> claimed;
        using (var counter = new StatementCounter(Database))
        {
            claimed = await intents.ClaimAsync(due, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
            statements = counter.Count;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{claimed.Count} rows claimed in {statements} statements");
        Assert.True(statements is > 0 and < 10, $"claiming {Rows} rows took {statements} statements");
        Assert.Equal(due.Where(intent => intent.Id != taken.Id).Select(intent => intent.Id).Order(), claimed.Order());
        await using (var context = await store.CreateContextAsync())
        {
            var attempts = await context.Set<OutboxEntry>().AsNoTracking().Select(e => e.AttemptCount).ToListAsync(TestContext.Current.CancellationToken);
            Assert.All(attempts, attempt => Assert.Equal(1, attempt));
        }
    }

    /// <summary>Counts the commands EF executes against one database while it is alive.</summary>
    private sealed class StatementCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _database;
        private readonly IDisposable _listeners;
        private readonly List<IDisposable> _subscriptions = [];
        private int _count;

        public StatementCounter(string database)
        {
            _database = database;
            _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int Count => Volatile.Read(ref _count);

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == DbLoggerCategory.Name)
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuted.Name && value.Value is CommandExecutedEventData executed && executed.Command.Connection?.Database == _database)
            {
                Interlocked.Increment(ref _count);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            _listeners.Dispose();
            lock (_subscriptions)
            {
                _subscriptions.ForEach(subscription => subscription.Dispose());
            }
        }
    }
}
