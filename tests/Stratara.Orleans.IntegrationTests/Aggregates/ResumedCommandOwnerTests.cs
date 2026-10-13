using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// A command that names the owner of its protected fields is resumed from its record for that owner, on the PostgreSQL
/// store: recorded from a session without a tenant and left as a host that died before the hand-over leaves it, the
/// drain resumes it, the activation reads its protected field for the owner the record names, and the handler runs
/// under the recorded session (scenario <em>A command naming an owner is resumed after a crash</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ResumedCommandOwnerTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string Secret = "https://example.test/a-page-somebody-visited";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_resumed_command_naming_an_owner_from_a_session_without_a_tenant_reaches_its_handler_with_its_protected_field()
    {
        var received = new OwnedProbeReceipts();
        var store = postgres.ConnectionStringFor("poc_resume_owner");
        var settings = new RecordedIntentSettings(
            store, postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit.ConnectionString, 11373, 30263, new RecordedIntentProbes(),
            PollingInterval: TimeSpan.FromSeconds(1), BatchSize: 100,
            Services: services => services
                .AddSingleton(received)
                .AddScoped<ICommandHandler<OwnedProbe>, OwnedProbeHandler>()
                .AddTrustedType<OwnedProbe>());
        using var host = await RecordedIntentHost.StartAsync(settings);
        var owner = Guid.NewGuid();
        var probe = Guid.NewGuid();
        var session = SessionContext.Empty();

        Guid intent;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(session);
            intent = Guid.CreateVersion7();
            await scope.ServiceProvider.GetRequiredService<IntentRecorder>().RecordAsync(
                new IntentDispatch(intent, session, AggregateId: null, Heavy: false, scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow()),
                new OwnedProbe(probe, owner, Secret),
                CancellationToken.None);
        }

        var record = await RecordedIntentHost.ScalarAsync<string>(store, "SELECT data_json FROM outbox_entry WHERE id = @id", ("id", intent));
        Assert.NotNull(record);
        Assert.Contains($"\"DataOwnerTenantId\":\"{owner}\"", record);
        Assert.DoesNotContain(Secret, record);

        Assert.True(await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(received.Ran.ContainsKey(probe)), RunTimeout), "the recorded command was not resumed");
        var (secret, ranUnderTenant) = received.Ran[probe];
        Assert.Equal(Secret, secret);
        Assert.Equal(Guid.Empty, ranUnderTenant);
        await host.StopAsync();
    }
}

/// <summary>A command that names the tenant its protected field belongs to, whatever session records it.</summary>
public sealed record OwnedProbe(Guid ProbeId, Guid Tenant, [property: EncryptData(DataSensitivityLevel.TenantScoped)] string? Secret)
    : ICommand, INamesDataOwner
{
    Guid INamesDataOwner.DataOwnerTenantId => Tenant;
}

/// <summary>What each owned probe's handler received, and the tenant of the session it ran under.</summary>
public sealed class OwnedProbeReceipts
{
    public ConcurrentDictionary<Guid, (string? Secret, Guid SessionTenant)> Ran { get; } = new();
}

public sealed class OwnedProbeHandler(OwnedProbeReceipts receipts, ISessionContextProvider sessions) : ICommandHandler<OwnedProbe>
{
    public Task HandleAsync(OwnedProbe command, CancellationToken cancellationToken)
    {
        receipts.Ran[command.ProbeId] = (command.Secret, sessions.Current?.TenantId ?? Guid.Empty);
        return Task.CompletedTask;
    }
}
