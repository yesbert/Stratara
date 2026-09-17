using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Scenario <em>A caller's own unit of work fails after the dispatch</em>: the record of an accepted command is committed
/// in a transaction of its own, so a dispatch made inside a unit of work the caller then abandons still runs.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class IntentTransactionTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_dispatch_inside_a_unit_of_work_the_caller_abandons_is_recorded_and_runs()
    {
        var probes = new RecordedIntentProbes();
        using var host = await RecordedIntentHost.StartAsync(new RecordedIntentSettings(
            postgres.ConnectionStringFor("poc_intent_transaction"), postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit.ConnectionString,
            11343, 30233, probes, PollingInterval: TimeSpan.FromSeconds(1), BatchSize: 100));
        var probe = Guid.NewGuid();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
            await using var transaction = await unitOfWork.StartAsync();
            await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new TenantProbe(Guid.NewGuid(), probe));
            // The caller's own work fails here: the transaction is disposed without a commit.
        }

        Assert.True(await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(probes.Ran.ContainsKey(probe)), Timeout), "the command dispatched inside the abandoned unit of work did not run");
        await host.StopAsync();
    }
}
