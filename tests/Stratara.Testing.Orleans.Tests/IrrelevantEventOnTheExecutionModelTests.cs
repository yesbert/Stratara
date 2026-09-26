using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Projections.Abstractions;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// <c>projections</c> → <em>A projection declares the events it cares about by handling them</em>, on the Orleans
/// execution model: a store reader reads past an event no projection in the host handles, of a type the host never
/// registered, instead of stalling its partition — verified on the in-process host over SQLite.
/// </summary>
public sealed class IrrelevantEventOnTheExecutionModelTests
{
    public sealed record AccountAudited(Guid AccountId, string Note);

    [Fact]
    public async Task A_store_reader_reads_past_an_event_no_projection_handles_of_a_type_never_registered()
    {
        var runs = new Runs();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services
            .AddSingleton(runs)
            .AddAggregatesFromAssemblyContaining<Account>()
            .AddScoped<IProjection, BalanceProjection>()
            .AddStrataraProjectionGrains());
        var account = Guid.NewGuid();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Account>(account, new AccountOpened(account, ExecutionModelTestHost.DefaultTenantId, 10m));
            await events.AppendAsync<Account>(account, new AccountAudited(account, "reviewed"));
            await events.AppendAsync<Account>(account, new AmountDeposited(account, 5m));
            await events.SaveChangesAsync();
        }

        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(15m, runs.Balances[account]);
    }
}
