using Stratara.Sample.EventSourced.Domain;
using Stratara.Sample.EventSourced.EventStore;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.EventSourced.Commands;

public sealed record OpenAccountCommand(string OwnerName, decimal InitialBalance) : ICommand<Guid>;

public sealed class OpenAccountCommandHandler(InMemoryEventStore store, TimeProvider clock)
    : IQueryHandler<OpenAccountCommand, Guid>
{
    public async Task<Guid> HandleAsync(OpenAccountCommand command, CancellationToken cancellationToken)
    {
        var accountId = Guid.NewGuid();
        store.Append(accountId, new AccountOpened(accountId, command.OwnerName, command.InitialBalance, clock.GetUtcNow()));
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return accountId;
    }
}
