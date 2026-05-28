using Stratara.Sample.AspNetCoreApi.Domain;
using Stratara.Sample.AspNetCoreApi.Infrastructure;
using Stratara.Abstractions.Mediator;

namespace Stratara.Sample.AspNetCoreApi.Commands;

public sealed record OpenAccountCommand(string OwnerName, decimal InitialBalance) : ICommand<Guid>;

public sealed class OpenAccountCommandHandler(InMemoryAccountRepository accounts, TimeProvider clock)
    : IQueryHandler<OpenAccountCommand, Guid>
{
    public Task<Guid> HandleAsync(OpenAccountCommand command, CancellationToken cancellationToken)
    {
        var account = new Account(
            id: Guid.NewGuid(),
            ownerName: command.OwnerName,
            initialBalance: command.InitialBalance,
            openedAt: clock.GetUtcNow());

        accounts.Save(account);
        return Task.FromResult(account.Id);
    }
}
