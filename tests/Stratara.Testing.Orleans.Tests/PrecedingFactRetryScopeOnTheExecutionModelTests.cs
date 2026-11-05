using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Testing.Orleans.Tests;

/// <summary>
/// <c>projections</c> and <c>sagas</c>, on the Orleans execution model: an entry retried after a missing preceding fact
/// is applied again from a fresh scope, as a bus delivery is, so what the failed attempt left in a scoped dependency
/// does not reach the retry — verified on the in-process host over SQLite.
/// </summary>
public sealed class PrecedingFactRetryScopeOnTheExecutionModelTests
{
    /// <summary>What the handlers wrote, and how often each stream's deposit was attempted.</summary>
    public sealed class Ledger
    {
        public ConcurrentDictionary<Guid, int> Attempts { get; } = new();

        public ConcurrentQueue<Guid> Written { get; } = new();
    }

    /// <summary>Stands in for a change tracker: what is staged in one scope is flushed by whoever saves in it.</summary>
    public sealed class StagedWrites
    {
        private readonly List<Guid> _staged = [];

        public void Stage(Guid id) => _staged.Add(id);

        public void FlushTo(Ledger ledger)
        {
            foreach (var id in _staged)
            {
                ledger.Written.Enqueue(id);
            }

            _staged.Clear();
        }
    }

    /// <summary>Stages the deposit, then reports its prerequisite missing on the first attempt only.</summary>
    private static Task StageAndSaveAsync(Ledger ledger, StagedWrites staged, Guid streamId)
    {
        staged.Stage(streamId);
        if (ledger.Attempts.AddOrUpdate(streamId, 1, static (_, n) => n + 1) == 1)
        {
            throw new PrecedingFactMissingException(streamId, nameof(AmountDeposited));
        }

        staged.FlushTo(ledger);
        return Task.CompletedTask;
    }

    public sealed class DepositProjection(Ledger ledger, StagedWrites staged) : IProjection
    {
        public Task HandleAsync(IEvent<AmountDeposited> @event, CancellationToken cancellationToken) =>
            StageAndSaveAsync(ledger, staged, @event.StreamId);
    }

    public sealed class DepositSaga(Ledger ledger, StagedWrites staged) : ISaga
    {
        public Task HandleAsync(IEvent<AmountDeposited> @event, CancellationToken cancellationToken) =>
            StageAndSaveAsync(ledger, staged, @event.StreamId);
    }

    [Fact]
    public async Task A_projection_entry_retried_after_a_missing_preceding_fact_is_applied_from_a_fresh_scope()
    {
        var ledger = new Ledger();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services
            .AddSingleton(ledger)
            .AddScoped<StagedWrites>()
            .AddAggregatesFromAssemblyContaining<Account>()
            .AddScoped<IProjection, DepositProjection>()
            .AddStrataraProjectionGrains());

        var account = await DepositAsync(host);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, ledger.Attempts[account]);
        Assert.Equal([account], ledger.Written.ToArray());
    }

    [Fact]
    public async Task A_saga_entry_retried_after_a_missing_preceding_fact_is_applied_from_a_fresh_scope()
    {
        var ledger = new Ledger();
        await using var host = await ExecutionModelTestHost.CreateAsync(services => services
            .AddSingleton(ledger)
            .AddScoped<StagedWrites>()
            .AddAggregatesFromAssemblyContaining<Account>()
            .AddScoped<ISaga, DepositSaga>()
            .AddStrataraSagaGrains());

        var account = await DepositAsync(host);
        await host.WaitForReadersAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, ledger.Attempts[account]);
        Assert.Equal([account], ledger.Written.ToArray());
    }

    private static async Task<Guid> DepositAsync(ExecutionModelTestHost host)
    {
        var account = Guid.NewGuid();
        await using var scope = host.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Account>(account, new AccountOpened(account, ExecutionModelTestHost.DefaultTenantId, 10m));
        await events.AppendAsync<Account>(account, new AmountDeposited(account, 5m));
        await events.SaveChangesAsync();
        return account;
    }
}
