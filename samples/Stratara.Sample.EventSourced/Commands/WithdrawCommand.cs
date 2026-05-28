using Stratara.Sample.EventSourced.Domain;
using Stratara.Sample.EventSourced.EventStore;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.EventSourced.Commands;

public sealed record WithdrawCommand(Guid AccountId, decimal Amount) : ICommand;

public sealed class WithdrawCommandHandler(
    InMemoryEventStore store,
    AggregationService aggregation,
    TimeProvider clock) : ICommandHandler<WithdrawCommand>
{
    public async Task HandleAsync(WithdrawCommand command, CancellationToken cancellationToken)
    {
        var account = aggregation.Aggregate<Account>(command.AccountId);
        if (command.Amount > account.Balance)
        {
            throw new InsufficientBalanceException(command.AccountId, account.Balance, command.Amount);
        }

        store.Append(command.AccountId, new AmountWithdrawn(command.AccountId, command.Amount, clock.GetUtcNow()));
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
