using System.Collections.Concurrent;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Orleans.Sagas;
using Stratara.Projections.Abstractions;
using Stratara.Sagas.Abstractions;
using Stratara.Testing.Orleans;

namespace Stratara.Sample.OrleansExecutionModel;

// ── The aggregate ────────────────────────────────────────────────────────────────────────────────

public sealed record AccountOpened(Guid AccountId, Guid TenantId, string Owner, decimal InitialBalance) : IAggregateCreationEvent;

public sealed class Account : ITenantAggregate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Owner { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public void Apply(AccountOpened @event)
    {
        Id = @event.AccountId;
        TenantId = @event.TenantId;
        Owner = @event.Owner;
        Balance = @event.InitialBalance;
    }
}

// ── The command: it names its aggregate, so it runs in that aggregate's activation ─────────────────

public sealed record OpenAccount(Guid AggregateId, string Owner, decimal InitialBalance) : ICommand, IAggregateScopedCommand;

public sealed class OpenAccountHandler(IEventSource events, SampleLog log) : ICommandHandler<OpenAccount>
{
    public async Task HandleAsync(OpenAccount command, CancellationToken cancellationToken)
    {
        await events.CreateAsync<Account>(
            command.AggregateId,
            new AccountOpened(command.AggregateId, ExecutionModelTestHost.DefaultTenantId, command.Owner, command.InitialBalance),
            cancellationToken);
        await events.SaveChangesAsync(cancellationToken);

        // Inside a grain's turn the task scheduler is the activation's own.
        log.CommandScheduler = TaskScheduler.Current.GetType().Name;
    }
}

// ── The projection: it reads the store in commit order from a checkpoint ─────────────────────────

public sealed class BalanceProjection(SampleLog log) : IProjection
{
    public Task HandleAsync(IEvent<AccountOpened> @event, CancellationToken cancellationToken)
    {
        log.Balances[@event.Data.Owner] = @event.Data.InitialBalance;
        return Task.CompletedTask;
    }
}

// ── The process: a durable timeout a second after the account was opened ────────────────────────

public sealed record WelcomeScheduled(Guid AccountId, string Owner);

public sealed class WelcomeState : ISagaProcessState
{
    public Guid Id { get; set; }

    public string Owner { get; set; } = string.Empty;

    public bool Completed { get; set; }

    public void Apply(WelcomeScheduled @event)
    {
        Id = @event.AccountId;
        Owner = @event.Owner;
    }
}

public sealed class WelcomeProcess(SampleLog log) : SagaProcess<WelcomeState>
{
    public override bool Handles(IEvent @event) => @event.Data is AccountOpened;

    public override Guid CorrelationOf(IEvent @event) => ((AccountOpened)@event.Data).AccountId;

    public override Task HandleAsync(WelcomeState state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        if (state.Id == Guid.Empty && @event.Data is AccountOpened opened)
        {
            context.Emit(new WelcomeScheduled(opened.AccountId, opened.Owner));
            context.Schedule("welcome", DateTimeOffset.UtcNow.AddSeconds(1));
        }

        return Task.CompletedTask;
    }

    public override Task OnTimeoutAsync(WelcomeState state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        log.Welcomed.Enqueue(state.Owner);
        return Task.CompletedTask;
    }
}

/// <summary>What the handler, the projection and the process saw.</summary>
public sealed class SampleLog
{
    public string CommandScheduler { get; set; } = string.Empty;

    public ConcurrentDictionary<string, decimal> Balances { get; } = new();

    public ConcurrentQueue<string> Welcomed { get; } = new();
}
