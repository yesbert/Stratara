using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// <c>projections</c> and <c>sagas</c>, on the Orleans execution model: a projection reader, a stateless saga's reader
/// and a process's reader read past an event no handler in the host takes, of a type the host never registered,
/// instead of stalling their partition — verified on the in-process host over SQLite.
/// </summary>
public sealed class IrrelevantEventOnTheExecutionModelTests
{
    public sealed record AccountAudited(Guid AccountId, string Note);

    public sealed class Deposits
    {
        public ConcurrentDictionary<Guid, decimal> Seen { get; } = new();
    }

    /// <summary>A stateless saga that takes deposits only; its first entry in a partition is one it does not take.</summary>
    public sealed class DepositSaga(Deposits deposits) : ISaga
    {
        public Task HandleAsync(IEvent<AmountDeposited> @event, CancellationToken cancellationToken)
        {
            deposits.Seen[@event.StreamId] = @event.Data.Amount;
            return Task.CompletedTask;
        }
    }

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

    [Fact]
    public async Task A_stateless_saga_reader_reads_past_an_event_no_saga_handles_of_a_type_never_registered()
    {
        var deposits = new Deposits();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services
            .AddSingleton(deposits)
            .AddAggregatesFromAssemblyContaining<Account>()
            .AddScoped<ISaga, DepositSaga>()
            .AddStrataraSagaGrains());
        var account = Guid.NewGuid();

        await AppendHistoryAsync(host, account);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5m, deposits.Seen[account]);
    }

    [Fact]
    public async Task A_process_reader_reads_past_an_event_whose_type_never_resolves()
    {
        var runs = new Runs();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services
            .AddSingleton(runs)
            .AddAggregatesFromAssemblyContaining<Account>()
            .AddTrustedType<WelcomeState>()
            .AddTrustedType<WelcomeScheduled>()
            .AddScoped<ISaga, WelcomeProcess>()
            .AddStrataraSagaGrains());
        var account = Guid.NewGuid();

        await AppendHistoryAsync(host, account);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!runs.Timeouts.ContainsKey(account) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(10m, runs.Timeouts[account]);
    }

    private static async Task AppendHistoryAsync(ExecutionModelTestHost host, Guid account)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Account>(account, new AccountOpened(account, ExecutionModelTestHost.DefaultTenantId, 10m));
        await events.AppendAsync<Account>(account, new AccountAudited(account, "reviewed"));
        await events.AppendAsync<Account>(account, new AmountDeposited(account, 5m));
        await events.SaveChangesAsync();
    }
}
