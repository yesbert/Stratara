using Stratara.Sample.AspNetCoreApi.Infrastructure;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.AspNetCoreApi.Commands;

public sealed record WithdrawCommand(Guid AccountId, decimal Amount) : ICommand;

public sealed class WithdrawCommandHandler(InMemoryAccountRepository accounts)
    : ICommandHandler<WithdrawCommand>
{
    public Task HandleAsync(WithdrawCommand command, CancellationToken cancellationToken)
    {
        var account = accounts.Get(command.AccountId);
        account.Withdraw(command.Amount);
        accounts.Save(account);
        return Task.CompletedTask;
    }
}
