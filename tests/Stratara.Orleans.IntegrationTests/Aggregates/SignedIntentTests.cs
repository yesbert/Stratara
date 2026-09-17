using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Messaging;
using Stratara.Orleans.IntegrationTests.Fixtures;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// A host with a signer in strict mode records a command, and its stored session is altered before the drain finds
/// it due: the command is kept at once with the reason that its signature did not verify, and its handler never runs.
/// Restored and returned by an operator, it is verified again and runs (scenario <em>A recorded command's session is
/// altered in storage</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SignedIntentTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan KeptTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_recorded_command_whose_session_was_altered_is_kept_and_runs_once_restored_and_returned()
    {
        var store = postgres.ConnectionStringFor("poc_signed_intent_store");
        var probes = new RecordedIntentProbes();
        using var host = await RecordedIntentHost.StartAsync(new RecordedIntentSettings(
            store, postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit.ConnectionString, 11255, 30145, probes,
            PollingInterval: TimeSpan.FromSeconds(1), BatchSize: 100, IntegrityMode: BusEnvelopeIntegrityMode.Strict));
        var dispatchedTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var probe = Guid.NewGuid();

        var intent = await RecordedIntentHost.RecordAsync(host, dispatchedTenant, probe);
        var altered = await RecordedIntentHost.ScalarAsync<int>(store,
            "WITH changed AS (UPDATE outbox_entry SET data_json = replace(data_json, @from, @to) WHERE id = @id RETURNING 1) SELECT count(*)::int FROM changed",
            ("from", dispatchedTenant.ToString()), ("to", otherTenant.ToString()), ("id", intent));
        Assert.Equal(1, altered);

        var kept = await RecordedIntentHost.WaitUntilAsync(
            async () => await RecordedIntentHost.ScalarAsync<bool>(store, "SELECT kept_at IS NOT NULL FROM outbox_entry WHERE id = @id", ("id", intent)),
            KeptTimeout);
        Assert.False(probes.Ran.TryGetValue(probe, out var ran), $"the handler ran under tenant {ran.Tenant} (dispatched under {dispatchedTenant}, altered to {otherTenant})");
        Assert.True(kept, "the altered command was not kept");
        var reason = await RecordedIntentHost.ScalarAsync<string>(store, "SELECT last_failure FROM outbox_entry WHERE id = @id", ("id", intent));
        Assert.Contains("does not verify", reason, StringComparison.Ordinal);
        Assert.Equal(0, await RecordedIntentHost.ScalarAsync<int>(store, "SELECT attempt_count FROM outbox_entry WHERE id = @id", ("id", intent)));

        await RecordedIntentHost.ScalarAsync<int>(store,
            "UPDATE outbox_entry SET data_json = replace(data_json, @from, @to), kept_at = NULL, attempt_count = 0 WHERE id = @id",
            ("from", otherTenant.ToString()), ("to", dispatchedTenant.ToString()), ("id", intent));

        Assert.True(await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(probes.Ran.ContainsKey(probe)), KeptTimeout), "the restored and returned command did not run");
        Assert.Equal(dispatchedTenant, probes.Ran[probe].Tenant);
        await host.StopAsync();
    }
}
