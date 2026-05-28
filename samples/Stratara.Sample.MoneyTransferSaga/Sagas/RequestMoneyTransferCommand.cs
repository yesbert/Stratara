using Stratara.Sample.MoneyTransferSaga.Commands;
using Stratara.Sample.MoneyTransferSaga.Domain;
using Stratara.Sample.MoneyTransferSaga.Infrastructure;
using Stratara.Sample.MoneyTransferSaga.Outbox;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.MoneyTransferSaga.Sagas;

public sealed record RequestMoneyTransferCommand(Guid SourceAccountId, Guid DestinationAccountId, decimal Amount)
    : ICommand;

public sealed class MoneyTransferSagaHandler(InMemoryAccountRepository accounts, CommandOutboxDispatcher dispatcher)
    : ICommandHandler<RequestMoneyTransferCommand>
{
    public Task HandleAsync(RequestMoneyTransferCommand command, CancellationToken cancellationToken)
    {
        var source = accounts.Get(command.SourceAccountId);
        if (command.Amount > source.Balance)
        {
            throw new InsufficientBalanceException(source.Id, source.Balance, command.Amount);
        }

        dispatcher.Enqueue(new WithdrawCommand(command.SourceAccountId, command.Amount));
        dispatcher.Enqueue(new DepositCommand(command.DestinationAccountId, command.Amount));
        return Task.CompletedTask;
    }
}
