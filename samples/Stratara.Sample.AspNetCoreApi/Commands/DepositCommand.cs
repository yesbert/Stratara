using Stratara.Sample.AspNetCoreApi.Infrastructure;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.AspNetCoreApi.Commands;

public sealed record DepositCommand(Guid AccountId, decimal Amount) : ICommand;

public sealed class DepositCommandHandler(InMemoryAccountRepository accounts)
    : ICommandHandler<DepositCommand>
{
    public Task HandleAsync(DepositCommand command, CancellationToken cancellationToken)
    {
        var account = accounts.Get(command.AccountId);
        account.Deposit(command.Amount);
        accounts.Save(account);
        return Task.CompletedTask;
    }
}
