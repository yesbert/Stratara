using Stratara.Sample.EventSourced.Domain;
using Stratara.Sample.EventSourced.EventStore;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.EventSourced.Commands;

public sealed record DepositCommand(Guid AccountId, decimal Amount) : ICommand;

public sealed class DepositCommandHandler(InMemoryEventStore store, TimeProvider clock)
    : ICommandHandler<DepositCommand>
{
    public async Task HandleAsync(DepositCommand command, CancellationToken cancellationToken)
    {
        store.Append(command.AccountId, new AmountDeposited(command.AccountId, command.Amount, clock.GetUtcNow()));
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
