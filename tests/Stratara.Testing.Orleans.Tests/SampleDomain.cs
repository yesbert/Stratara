using System.Collections.Concurrent;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.Sagas;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;

namespace Stratara.Testing.Orleans.Tests;

public sealed record AccountOpened(Guid AccountId, Guid TenantId, decimal InitialBalance) : IAggregateCreationEvent;

public sealed record AmountDeposited(Guid AccountId, decimal Amount);

public sealed class Account : ITenantAggregate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public decimal Balance { get; set; }

    public void Apply(AccountOpened @event)
    {
        Id = @event.AccountId;
        TenantId = @event.TenantId;
        Balance = @event.InitialBalance;
    }

    public void Apply(AmountDeposited @event) => Balance += @event.Amount;
}

public sealed record OpenAccount(Guid AggregateId, decimal InitialBalance) : ICommand, IAggregateScopedCommand;

/// <summary>Records the activation the command ran in, so a test can tell it ran in its aggregate's grain.</summary>
public sealed class OpenAccountHandler(IEventSource events, Runs runs) : ICommandHandler<OpenAccount>
{
    public async Task HandleAsync(OpenAccount command, CancellationToken cancellationToken)
    {
        await events.CreateAsync<Account>(command.AggregateId, new AccountOpened(command.AggregateId, ExecutionModelTestHost.DefaultTenantId, command.InitialBalance), cancellationToken);
        await events.SaveChangesAsync(cancellationToken);
        runs.Commands[command.AggregateId] = TaskScheduler.Current.GetType().Name;
    }
}

/// <summary>What the test's handlers, projections, processes and timers saw.</summary>
public sealed class Runs
{
    public ConcurrentDictionary<Guid, string> Commands { get; } = new();

    public ConcurrentDictionary<Guid, decimal> Balances { get; } = new();

    public ConcurrentQueue<(string OwnerId, string Purpose)> Timers { get; } = new();

    public ConcurrentDictionary<Guid, decimal> Timeouts { get; } = new();

    /// <summary>How often the projection applied an entry, so a test can see an entry applied a second time.</summary>
    public int Applied => Volatile.Read(ref _applied);

    private int _applied;

    public void MarkApplied() => Interlocked.Increment(ref _applied);
}

public sealed class BalanceProjection(Runs runs) : IProjection
{
    public Task HandleAsync(IEvent<AccountOpened> @event, CancellationToken cancellationToken)
    {
        runs.Balances[@event.StreamId] = @event.Data.InitialBalance;
        runs.MarkApplied();
        return Task.CompletedTask;
    }

    public Task HandleAsync(IEvent<AmountDeposited> @event, CancellationToken cancellationToken)
    {
        runs.Balances.AddOrUpdate(@event.StreamId, @event.Data.Amount, (_, balance) => balance + @event.Data.Amount);
        runs.MarkApplied();
        return Task.CompletedTask;
    }
}

public sealed class TimerPorts(Runs runs) : ITimerOwners, ITimerHandler
{
    public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task OnDueAsync(TimerDue due, CancellationToken cancellationToken)
    {
        runs.Timers.Enqueue((due.OwnerId, due.Purpose));
        return Task.CompletedTask;
    }
}

public sealed record WelcomeScheduled(Guid AccountId, decimal Balance);

public sealed class WelcomeState : ISagaProcessState
{
    public Guid Id { get; set; }

    public decimal Balance { get; set; }

    public bool Completed { get; set; }

    public void Apply(WelcomeScheduled @event)
    {
        Id = @event.AccountId;
        Balance = @event.Balance;
    }
}

/// <summary>Schedules a welcome a second after an account is opened, and records the state the timeout sees.</summary>
public sealed class WelcomeProcess(Runs runs) : SagaProcess<WelcomeState>
{
    public override bool Handles(IEvent @event) => @event.Data is AccountOpened;

    public override Guid CorrelationOf(IEvent @event) => ((AccountOpened)@event.Data).AccountId;

    public override Task HandleAsync(WelcomeState state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        if (state.Id == Guid.Empty && @event.Data is AccountOpened opened)
        {
            context.Emit(new WelcomeScheduled(opened.AccountId, opened.InitialBalance));
            context.Schedule("welcome", DateTimeOffset.UtcNow.AddSeconds(1));
        }

        return Task.CompletedTask;
    }

    public override Task OnTimeoutAsync(WelcomeState state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        runs.Timeouts[state.Id] = state.Balance;
        return Task.CompletedTask;
    }
}
