using Stratara.Sample.MoneyTransferSaga.Domain;
using Stratara.Sample.MoneyTransferSaga.Infrastructure;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.MoneyTransferSaga.Commands;

public sealed record OpenAccountCommand(Guid AccountId, string OwnerName, decimal InitialBalance) : ICommand;

public sealed class OpenAccountCommandHandler(InMemoryAccountRepository accounts, TimeProvider clock)
    : ICommandHandler<OpenAccountCommand>
{
    public Task HandleAsync(OpenAccountCommand command, CancellationToken cancellationToken)
    {
        var account = new Account(command.AccountId, command.OwnerName, command.InitialBalance, clock.GetUtcNow());
        accounts.Save(account);
        return Task.CompletedTask;
    }
}
